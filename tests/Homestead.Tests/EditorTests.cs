using System.Net;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class EditorTests
{
    [Theory]
    [InlineData(PostType.Article)]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    public async Task Opening_a_new_editor_and_saving_whitespace_does_not_store_a_draft(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var created = await TestApplication.Submit(client, "/admin/posts", new() { ["type"] = type.ToString() }, "/admin/posts?handler=New");
        var path = created.Headers.Location!.ToString();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        await app.WithDatabase(async db => { Assert.Empty(await db.Posts.ToListAsync()); Assert.Empty(await db.PostDrafts.ToListAsync()); });
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var response = await TestApplication.Submit(client, path, new()
        {
            ["NewPostType"] = type.ToString(),
            ["Input.Version"] = "0",
            ["Input.Title"] = "  ",
            ["Input.BodyMarkdown"] = " \n ",
            ["Input.Summary"] = " ",
            ["Input.Subject"] = " ",
            ["Input.Tags"] = " , ",
            ["Input.LocationName"] = " "
        }, path.Split('?')[0] + "?handler=Save");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await app.WithDatabase(async db => { Assert.Empty(await db.Posts.ToListAsync()); Assert.Empty(await db.PostDrafts.ToListAsync()); });
        Assert.DoesNotContain("Untitled", await client.GetStringAsync("/admin/posts"));
    }

    [Theory]
    [InlineData(PostType.Article, "Input.Title", "A title")]
    [InlineData(PostType.Article, "Input.Summary", "A summary")]
    [InlineData(PostType.Microblog, "Input.BodyMarkdown", "A thought")]
    [InlineData(PostType.Review, "Input.Subject", "A subject")]
    [InlineData(PostType.Review, "Input.Rating", "3")]
    [InlineData(PostType.Review, "Input.ExternalUrl", "https://example.test/item")]
    [InlineData(PostType.Guide, "Input.Steps[0].BodyMarkdown", "An instruction")]
    [InlineData(PostType.Gallery, "Input.Title", "A gallery")]
    [InlineData(PostType.Checkin, "Input.LocationName", "A place")]
    [InlineData(PostType.Checkin, "Input.LocationAddress", "An address")]
    [InlineData(PostType.Microblog, "Input.Tags", "A tag")]
    public async Task First_content_saves_once_stale_tabs_conflict_and_clearing_a_saved_draft_preserves_it(PostType type, string field, string value)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var created = await TestApplication.Submit(client, "/admin/posts", new() { ["type"] = type.ToString() }, "/admin/posts?handler=New");
        var path = created.Headers.Location!.ToString(); var savedPath = path.Split('?')[0];
        var fields = new Dictionary<string, string> { ["NewPostType"] = type.ToString(), ["Input.Version"] = "0", [field] = value };
        if (type == PostType.Guide) fields["Input.Steps[0].Id"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, path, fields, savedPath + "?handler=Save")).StatusCode);
        await app.WithDatabase(async db =>
        {
            Assert.Equal(type, (await db.Posts.SingleAsync()).Type);
            Assert.Equal(1, (await db.PostDrafts.SingleAsync()).Version);
        });
        Assert.Equal(HttpStatusCode.Conflict, (await TestApplication.Submit(client, path, fields, savedPath + "?handler=Save")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, savedPath, new() { ["Input.Version"] = "1" }, savedPath + "?handler=Save")).StatusCode);
        await app.WithDatabase(async db =>
        {
            Assert.Single(await db.Posts.ToListAsync());
            var draft = await db.PostDrafts.SingleAsync(); Assert.Equal(2, draft.Version); Assert.Equal("", draft.BodyMarkdown);
        });
    }

    [Fact]
    public async Task Empty_guide_steps_and_invalid_publication_do_not_create_drafts()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var created = await TestApplication.Submit(client, "/admin/posts", new() { ["type"] = "Guide" }, "/admin/posts?handler=New");
        var path = created.Headers.Location!.ToString(); var savedPath = path.Split('?')[0];
        var fields = new Dictionary<string, string> { ["NewPostType"] = "Guide", ["Input.Version"] = "0", ["command"] = "add-step" };
        var arranged = await TestApplication.Submit(client, path, fields, savedPath + "?handler=Arrange");
        Assert.Equal(HttpStatusCode.OK, arranged.StatusCode);
        Assert.Contains("Step 1 instructions", await arranged.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, path, fields, savedPath + "?handler=Publish")).StatusCode);
        await app.WithDatabase(async db => { Assert.Empty(await db.Posts.ToListAsync()); Assert.Empty(await db.GuideSteps.ToListAsync()); });
    }

    [Theory]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    public async Task First_photo_creates_a_draft_only_after_a_successful_upload(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var created = await TestApplication.Submit(client, "/admin/posts", new() { ["type"] = type.ToString() }, "/admin/posts?handler=New");
        var path = created.Headers.Location!.ToString(); var html = await client.GetStringAsync(path);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        using var bytes = new MemoryStream(TestImages.Create());
        foreach (var valid in new[] { false, true })
        {
            using var form = new MultipartFormDataContent(); form.Add(new StringContent(token), "__RequestVerificationToken");
            var file = new ByteArrayContent(valid ? bytes.ToArray() : "broken image"u8.ToArray()); file.Headers.ContentType = new("image/png"); form.Add(file, "Upload", "photo.png");
            Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, (await client.PostAsync(path + "&handler=Upload", form)).StatusCode);
            await app.WithDatabase(async db =>
            {
                Assert.Equal(valid ? 1 : 0, await db.Posts.CountAsync()); Assert.Equal(valid ? 1 : 0, await db.PostDrafts.CountAsync());
                Assert.Equal(valid ? 1 : 0, await db.MediaAssets.CountAsync());
            });
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path.Split('?')[0])).StatusCode);
    }

    [Theory]
    [InlineData(PostType.Article)]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    public async Task Removed_inline_images_leave_preview_and_next_publication_but_remain_private_for_undo(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var post = await articles.CreateAsync(type);
        var image = await Photo(media, post.Id, true);
        var manual = await Photo(media, post.Id, false);
        var input = new ArticleInput { Title = "Editor example", Subject = "Example", Rating = 4, LocationName = "Example place", BodyMarkdown = $"Text ![sky]({image.Url})" };
        if (type == PostType.Guide) input.Steps.Add(new() { BodyMarkdown = "Follow instructions" });
        input.Version = await articles.SaveAsync(post.Id, input);
        Assert.True((await publisher.TransitionAsync(post.Id, "publish", input.Version)).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.Url)).StatusCode);
        var live = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
        Assert.Contains(image.Id, live.Media.Select(x => x.Id));

        // Stale tabs must not remove a newer inline reference.
        var stale = new ArticleInput { Version = 0, Title = "Stale", BodyMarkdown = "Removed" };
        await Assert.ThrowsAsync<DraftConflictException>(() => articles.SaveAsync(post.Id, stale));
        Assert.Contains(image.PrivateUrl, await client.GetStringAsync($"/admin/posts/{post.Id}/preview"));

        // An image URL inside a code fence is not an image reference.
        input.BodyMarkdown = $"Text\n\n```\n![sky]({image.Url})\n```";
        input.Version = await articles.SaveAsync(post.Id, input);
        Assert.DoesNotContain($"src=\"{image.PrivateUrl}\"", await client.GetStringAsync($"/admin/posts/{post.Id}/preview"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.Url)).StatusCode); // Active generation is unchanged.
        Assert.True((await publisher.TransitionAsync(post.Id, "publish", input.Version)).Succeeded);
        var updated = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
        Assert.DoesNotContain(image.Id, updated.Media.Select(x => x.Id));
        if (type != PostType.Article) Assert.Contains(manual.Id, updated.Media.Select(x => x.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(image.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.PrivateUrl)).StatusCode);
        await app.WithDatabase(async db => Assert.True((await db.PostMedia.SingleAsync(x => x.MediaAssetId == image.Id)).InlineOnly));

        // Reinsertion, including reference-style Markdown in a guide step, restores it.
        var restored = $"![sky][photo]\n\n[photo]: {image.Url}";
        if (type == PostType.Guide) input.Steps[0].BodyMarkdown = restored;
        else input.BodyMarkdown = restored;
        input.Version = await articles.SaveAsync(post.Id, input);
        Assert.Contains(image.PrivateUrl, await client.GetStringAsync($"/admin/posts/{post.Id}/preview"));
        Assert.True((await publisher.TransitionAsync(post.Id, "publish", input.Version)).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.Url)).StatusCode);
    }

    [Theory]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Gallery)]
    public async Task Unreferenced_inline_uploads_do_not_satisfy_photo_publication_requirements(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>(); var post = await articles.CreateAsync(type);
        _ = await Photo(scope.ServiceProvider.GetRequiredService<MediaService>(), post.Id, true);
        Assert.False((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", 0)).Succeeded);
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    [Fact]
    public async Task Inline_upload_handler_marks_private_attachment_and_editor_keeps_no_script_fallback()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var created = await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=New");
        var path = created.Headers.Location!.ToString(); var html = await client.GetStringAsync(path);
        Assert.Contains("id=\"write-tab\"", html); Assert.Contains("id=\"preview-tab\"", html);
        Assert.Contains("aria-label=\"Bold (Ctrl/", html); Assert.Contains("/icons/toolbar.svg#bold", html);
        Assert.Contains("data-preview-url", html); Assert.Contains("enctype=\"multipart/form-data\"", html);
        Assert.Contains("id=\"images-tab\"", html); Assert.Contains("aria-label=\"Post settings\"", html);
        Assert.Contains("id=\"details-tab\"", html); Assert.Contains("id=\"editor-details\"", html);
        Assert.Contains("id=\"delete-post-dialog\"", html); Assert.DoesNotContain("Open preview", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("true"), "InlineUpload");
        using var bytes = new MemoryStream(TestImages.Create());
        var file = new ByteArrayContent(bytes.ToArray()); file.Headers.ContentType = new("image/png"); form.Add(file, "Upload", "clipboard.png");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path + "&handler=Upload", form)).StatusCode);
        await app.WithDatabase(async db => Assert.True((await db.PostMedia.SingleAsync()).InlineOnly));
        Assert.Contains("<symbol id=\"bold\"", await client.GetStringAsync("/icons/toolbar.svg"));
    }

    private static async Task<ArticleMedia> Photo(MediaService media, Guid id, bool inlineOnly)
    {
        using var bytes = new MemoryStream(TestImages.Create());
        return await media.UploadAsync(id, new FormFile(bytes, 0, bytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "Sky", inlineOnly);
    }
}
