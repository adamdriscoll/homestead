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
        var input = new ArticleInput { Title = "Editor example", Subject = "Example", Rating = 4, BodyMarkdown = $"Text ![sky]({image.Url})" };
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
        Assert.Contains("id=\"delete-post-dialog\"", html); Assert.DoesNotContain("Open preview", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("true"), "InlineUpload");
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(3, 2);
        using var bytes = new MemoryStream(); image.Save(bytes, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        var file = new ByteArrayContent(bytes.ToArray()); file.Headers.ContentType = new("image/png"); form.Add(file, "Upload", "clipboard.png");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path + "?handler=Upload", form)).StatusCode);
        await app.WithDatabase(async db => Assert.True((await db.PostMedia.SingleAsync()).InlineOnly));
        Assert.Contains("<symbol id=\"bold\"", await client.GetStringAsync("/icons/toolbar.svg"));
    }

    private static async Task<ArticleMedia> Photo(MediaService media, Guid id, bool inlineOnly)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(3, 2);
        using var bytes = new MemoryStream(); image.Save(bytes, new SixLabors.ImageSharp.Formats.Png.PngEncoder()); bytes.Position = 0;
        return await media.UploadAsync(id, new FormFile(bytes, 0, bytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "Sky", inlineOnly);
    }
}
