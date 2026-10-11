using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class FiveTypeTests
{
    private static async Task<Guid> Create(TestApplication app, PostType type)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(type)).Id;
    }

    private static async Task<ArticleMedia> Photo(TestApplication app, Guid id)
    {
        using var bytes = new MemoryStream(TestImages.Create());
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(id,
            new FormFile(bytes, 0, bytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "Garden & sky");
    }

    private static async Task<long> Version(TestApplication app, Guid id)
    {
        long version = 0;
        await app.WithDatabase(async db => version = (await db.PostDrafts.SingleAsync(x => x.PostId == id)).Version);
        return version;
    }

    private static Task<HttpResponseMessage> Action(HttpClient client, Guid id, string handler, Dictionary<string, string> fields)
        => TestApplication.Submit(client, $"/admin/posts/{id}/edit", fields, $"/admin/posts/{id}/edit?handler={handler}");

    // Fixed examples exercise each editor's persisted representation and public presentation.
    private static Dictionary<string, string> Fixture(PostType type, long version, string marker, ArticleMedia image, Guid step) => new()
    {
        ["Input.Version"] = version.ToString(),
        ["Input.Title"] = type == PostType.Review ? "" : marker + " title",
        ["Input.BodyMarkdown"] = marker + " **body**" + (type == PostType.Article ? $"\n\n![Garden]({image.Url})" : ""),
        ["Input.Tags"] = marker + " tag",
        ["Input.Subject"] = marker + " subject",
        ["Input.LocationName"] = marker + " place",
        ["Input.LocationAddress"] = marker + " address",
        ["Input.Latitude"] = "41.88",
        ["Input.Longitude"] = "-87.63",
        ["Input.Rating"] = "4",
        ["Input.ExternalUrl"] = "https://example.test/item?x=1&y=2",
        ["Input.AltText[" + image.Id + "]"] = marker + " alt",
        ["Input.Captions[" + image.Id + "]"] = marker + " caption",
        ["Input.Steps[0].Id"] = step.ToString(),
        ["Input.Steps[0].BodyMarkdown"] = marker + " **instructions**",
        ["Input.Steps[0].Duration"] = "01:30:00",
        ["Input.Tools[0].Name"] = marker + " tool",
        ["Input.Tools[0].Url"] = "https://example.test/tool",
        ["Input.Materials[0].Name"] = marker + " material",
        ["Input.StepImages[" + image.Id + "]"] = type == PostType.Guide ? step.ToString() : "",
        ["Input.MediaOrder[0]"] = image.Id.ToString()
    };

    [Theory]
    [InlineData(PostType.Microblog, "/notes/")]
    [InlineData(PostType.Article, "/articles/")]
    [InlineData(PostType.Review, "/reviews/")]
    [InlineData(PostType.Guide, "/guides/")]
    [InlineData(PostType.Gallery, "/galleries/")]
    [InlineData(PostType.Checkin, "/checkins/")]
    public async Task Every_type_completes_lifecycle_with_snapshot_discovery_and_media_isolation(PostType type, string listing)
    {
        using var data = new TemporaryData();
        Guid id; ArticleMedia image; ArticleSnapshot live; var step = Guid.NewGuid();
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        using (var visitor = app.Client())
        {
            _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
            id = await Create(app, type); image = await Photo(app, id);
            Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync(image.PrivateUrl)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(image.Url)).StatusCode);
            var fields = Fixture(type, 0, "Original", image, step);
            var save = await Action(author, id, "Save", fields);
            Assert.True(save.StatusCode == HttpStatusCode.Redirect, Regex.Match(await save.Content.ReadAsStringAsync(), "<div[^>]*validation-summary-errors.*?</div>", RegexOptions.Singleline).Value);
            var preview = await author.GetStringAsync($"/admin/posts/{id}/preview");
            Assert.Contains("Original", preview); Assert.Contains(image.PrivateUrl, preview);
            Assert.Contains("noindex, nofollow", preview);
            Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/admin/posts/{id}/preview")).StatusCode);
            var noToken = fields.Where(x => x.Key != "__RequestVerificationToken").ToDictionary();
            Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsync($"/admin/posts/{id}/edit?handler=Publish", new FormUrlEncodedContent(noToken))).StatusCode);
            fields["Input.Version"] = "1";
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Publish", fields)).StatusCode);
            live = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
            var path = $"/posts/{live.Slug}/";
            var html = await visitor.GetStringAsync(path);
            Assert.Contains("Original", html); Assert.Contains(image.Url, html); Assert.DoesNotContain("<script", html);
            Assert.Contains("Location: Original place", html); Assert.Contains("Original address", html);
            Assert.Equal(new LocationSnapshot("Original place", "Original address", 41.88, -87.63), live.Location);
            Assert.Contains("https://example.test" + path, html); Assert.Contains("og:image", html); Assert.Contains("twitter:card", html);
            Assert.DoesNotContain("Unpublished edits", await author.GetStringAsync($"/admin/posts/{id}/edit"));
            Assert.Contains("Original", await visitor.GetStringAsync(listing));
            Assert.Contains("Original", await visitor.GetStringAsync("/tags/original-tag/"));
            Assert.Contains("Original", await visitor.GetStringAsync("/"));
            var rss = XDocument.Parse(await visitor.GetStringAsync("/rss.xml"));
            XNamespace content = "http://purl.org/rss/1.0/modules/content/";
            Assert.Contains("https://example.test" + image.Url, rss.Descendants("item").Single().Element(content + "encoded")!.Value);
            Assert.Contains(path, await visitor.GetStringAsync("/sitemap.xml"));
            if (type == PostType.Review) { Assert.Contains("4/5", html); Assert.Contains("Original subject", html); }
            if (type == PostType.Guide)
            {
                Assert.Contains("<ol", html); Assert.Contains("<strong>instructions</strong>", html);
                Assert.Contains("Original tool", html); Assert.Contains("Original material", html); Assert.Contains("1 hour, 30 minutes", html);
            }
            if (type == PostType.Gallery) Assert.Contains("<figcaption>Original caption</figcaption>", html);
            var privatePhoto = await Photo(app, id);
            fields = Fixture(type, await Version(app, id), "PRIVATE", image, step);
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Save", fields)).StatusCode);
            Assert.Contains("Unpublished edits", await author.GetStringAsync($"/admin/posts/{id}/edit"));
            Assert.Equal(html, await visitor.GetStringAsync(path));
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(privatePhoto.Url)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, "/admin/posts", [], "/admin/posts?handler=Rebuild")).StatusCode);
            foreach (var file in app.Services.GetRequiredService<ActiveSite>().Current!.Files.Where(x => x.EndsWith(".html") || x.EndsWith(".xml")))
                Assert.DoesNotContain("PRIVATE", await visitor.GetStringAsync("/" + file));
            fields = Fixture(type, await Version(app, id), "Updated", image, step);
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Publish", fields)).StatusCode);
            Assert.Contains("Updated", await visitor.GetStringAsync(path)); Assert.DoesNotContain("Original", await visitor.GetStringAsync("/rss.xml"));
            var updated = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
            Assert.Equal(live.Slug, updated.Slug); Assert.Equal(live.PublishedAtUtc, updated.PublishedAtUtc);
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Unpublish", new() { ["Input.Version"] = (await Version(app, id)).ToString() })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(image.Url)).StatusCode);
            Assert.DoesNotContain(live.Slug, await visitor.GetStringAsync("/rss.xml")); Assert.DoesNotContain(live.Slug, await visitor.GetStringAsync("/sitemap.xml"));
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Publish", Fixture(type, await Version(app, id), "Republished", image, step))).StatusCode);
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var client = restarted.Client();
        Assert.Contains("Republished", await client.GetStringAsync($"/posts/{live.Slug}/"));
        _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Action(client, id, "Delete", new() { ["Input.Version"] = (await Version(restarted, id)).ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Delete", new() { ["Input.Version"] = (await Version(restarted, id)).ToString(), ["ConfirmDelete"] = "true" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(image.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/posts/{live.Slug}/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/admin/posts/{id}/edit")).StatusCode);
    }

    [Theory]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    public async Task Incomplete_specialized_drafts_save_preview_and_fail_publication(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app, type);
        var fields = new Dictionary<string, string> { ["Input.Version"] = "0", ["Input.Title"] = "Incomplete" };
        if (type == PostType.Guide) { fields["Input.Steps[0].Id"] = Guid.NewGuid().ToString(); fields["Input.Steps[0].BodyMarkdown"] = ""; }
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Save", fields)).StatusCode);
        Assert.Contains("Private draft preview", await client.GetStringAsync($"/admin/posts/{id}/preview"));
        fields["Input.Version"] = "1";
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Publish", fields)).StatusCode);
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    [Theory]
    [InlineData("", "3", "Text", "")]
    [InlineData("Subject", "", "Text", "")]
    [InlineData("Subject", "0", "Text", "")]
    [InlineData("Subject", "6", "Text", "")]
    [InlineData("Subject", "2.5", "Text", "")]
    [InlineData("Subject", "3", " ", "")]
    [InlineData("Subject", "3", "Text", "javascript:alert(1)")]
    [InlineData("Subject", "3", "Text", "//example.test")]
    [InlineData("Subject", "3", "Text", "https://user:pass@example.test")]
    public async Task Invalid_reviews_never_publish(string subject, string rating, string body, string url)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app, PostType.Review);
        var result = await Action(client, id, "Publish", new() { ["Input.Version"] = "0", ["Input.Subject"] = subject, ["Input.Rating"] = rating, ["Input.BodyMarkdown"] = body, ["Input.ExternalUrl"] = url });
        Assert.Contains(result.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Redirect });
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    [Theory]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    public async Task Ordered_content_photos_captions_and_removal_persist_without_javascript(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app, type);
        var first = await Photo(app, id); var second = await Photo(app, id); var step = Guid.NewGuid(); var nextStep = Guid.NewGuid();
        var fields = Fixture(type, 0, "First", first, step);
        fields["Input.MediaOrder[1]"] = second.Id.ToString(); fields[$"Input.Captions[{second.Id}]"] = "Second <script>caption</script>";
        fields[$"Input.AltText[{second.Id}]"] = "Second & sky";
        if (type == PostType.Guide) { fields["Input.Steps[1].Id"] = nextStep.ToString(); fields["Input.Steps[1].BodyMarkdown"] = "Second instructions"; fields[$"Input.StepImages[{second.Id}]"] = nextStep.ToString(); }
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Save", fields)).StatusCode);
        fields["Input.Version"] = "1"; fields["command"] = type == PostType.Guide ? "step-down:0" : $"photo-down:{first.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Arrange", fields)).StatusCode);
        await app.WithDatabase(async db =>
        {
            var post = await db.Posts.Include(x => x.Draft).Include(x => x.Steps).Include(x => x.Media).SingleAsync(x => x.Id == id);
            if (type == PostType.Guide) Assert.Equal(new[] { nextStep, step }, post.Steps.OrderBy(x => x.Order).Select(x => x.Id));
            else Assert.Equal(new[] { second.Id, first.Id }, post.Media.OrderBy(x => x.SortOrder).Select(x => x.MediaAssetId));
        });
        // Use the persisted editor form values after reordering, as an ordinary browser does.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ArticleService>(); var post = (await service.FindAsync(id))!;
            var result = await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(id, "publish", post.Draft!.Version);
            Assert.True(result.Succeeded, result.Message);
        }
        var live = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
        var html = await client.GetStringAsync($"/posts/{live.Slug}/");
        html = html[html.IndexOf("<main", StringComparison.Ordinal)..];
        Assert.True(html.IndexOf(second.Url, StringComparison.Ordinal) < html.IndexOf(first.Url, StringComparison.Ordinal));
        Assert.Contains("Second &amp; sky", html); Assert.Contains("&lt;script&gt;caption&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html);
        var preview = await client.GetStringAsync($"/admin/posts/{id}/preview");
        Assert.True(preview.IndexOf(second.PrivateUrl, StringComparison.Ordinal) < preview.IndexOf(first.PrivateUrl, StringComparison.Ordinal));
        var rss = XDocument.Parse(await client.GetStringAsync("/rss.xml")); XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        var rssHtml = rss.Descendants("item").Single().Element(content + "encoded")!.Value;
        Assert.True(rssHtml.IndexOf(second.Url, StringComparison.Ordinal) < rssHtml.IndexOf(first.Url, StringComparison.Ordinal));
        var formFields = new Dictionary<string, string>(fields) { ["Input.Version"] = (await Version(app, id)).ToString() };
        if (type == PostType.Guide)
        {
            formFields["Input.Steps[0].Id"] = nextStep.ToString(); formFields["Input.Steps[0].BodyMarkdown"] = "Second instructions";
            formFields["Input.Steps[1].Id"] = step.ToString(); formFields["Input.Steps[1].BodyMarkdown"] = "First **instructions**";
        }
        else { formFields["Input.MediaOrder[0]"] = second.Id.ToString(); formFields["Input.MediaOrder[1]"] = first.Id.ToString(); }
        // Removal saves the rest of the draft and leaves the active snapshot untouched.
        formFields["command"] = $"remove-photo:{second.Id}";
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Arrange", formFields)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(second.Url)).StatusCode);
        await app.WithDatabase(async db => Assert.False(await db.PostMedia.AnyAsync(x => x.PostId == id && x.MediaAssetId == second.Id)));
        await using var updateScope = app.Services.CreateAsyncScope();
        Assert.True((await updateScope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(id, "publish", await Version(app, id))).Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(second.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(first.Url)).StatusCode);
    }

    [Theory]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    public async Task Specialized_edits_conflict_reject_foreign_media_and_keep_markdown_safe(PostType type)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var id = await Create(app, type); var other = await Create(app, PostType.Guide);
        var own = await Photo(app, id); var foreign = await Photo(app, other); var step = Guid.NewGuid();
        var fields = Fixture(type, 0, "Safe", own, step);
        if (type == PostType.Guide) fields["Input.Steps[0].BodyMarkdown"] = "<script>evil()</script> [bad](javascript:alert(1))";
        else fields["Input.BodyMarkdown"] = "<script>evil()</script> [bad](javascript:alert(1))";
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Save", fields)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Action(client, id, "Save", fields)).StatusCode);
        fields["Input.Version"] = "1"; fields["Input.MediaOrder[0]"] = foreign.Id.ToString();
        Assert.Equal(HttpStatusCode.BadRequest, (await Action(client, id, "Save", fields)).StatusCode);
        fields["Input.MediaOrder[0]"] = own.Id.ToString();
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Publish", fields)).StatusCode);
        var live = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
        var html = await client.GetStringAsync($"/posts/{live.Slug}/");
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(foreign.Url)).StatusCode);
        if (type == PostType.Guide)
        {
            var otherFields = Fixture(PostType.Guide, 0, "Other", foreign, step);
            Assert.Equal(HttpStatusCode.BadRequest, (await Action(client, other, "Save", otherFields)).StatusCode);
        }
    }

    [Theory]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    public async Task Specialized_build_failures_keep_snapshots_and_retry_atomically(PostType type)
    {
        using var data = new TemporaryData(); var failure = new RenderFailure();
        using var app = new TestApplication(data.Path, configureServices: services =>
        { services.AddSingleton(failure); services.AddScoped<IStaticPageRenderer, FailingRenderer>(); });
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var id = await Create(app, type); var image = await Photo(app, id); var step = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Publish", Fixture(type, 0, "Original", image, step))).StatusCode);
        var live = app.Services.GetRequiredService<ActiveSite>().Current!;
        var html = await client.GetStringAsync($"/posts/{live.Articles.Single().Slug}/"); var rss = await client.GetStringAsync("/rss.xml");
        failure.Fail = true;
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Publish", Fixture(type, 1, "Desired", image, step))).StatusCode);
        Assert.Equal(live.GenerationId, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
        Assert.Equal(html, await client.GetStringAsync($"/posts/{live.Articles.Single().Slug}/")); Assert.Equal(rss, await client.GetStringAsync("/rss.xml"));
        Assert.Contains("previous post is still public", await client.GetStringAsync($"/admin/posts/{id}/edit"));
        failure.Fail = false;
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=Rebuild")).StatusCode);
        Assert.Contains("Desired", await client.GetStringAsync($"/posts/{live.Articles.Single().Slug}/"));
    }

    [Fact]
    public async Task Gallery_batch_upload_without_javascript_warns_about_alt_text_and_keeps_images_private()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app, PostType.Gallery);
        var path = $"/admin/posts/{id}/edit"; var html = await client.GetStringAsync(path);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(token), "__RequestVerificationToken");
        var bytes = TestImages.Create();
        foreach (var name in new[] { "first.png", "second.png" })
        { var file = new ByteArrayContent(bytes); file.Headers.ContentType = new("image/png"); form.Add(file, "Uploads", name); }
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(path + "?handler=Upload", form)).StatusCode);
        Assert.Contains("Some photos are missing alt text", await client.GetStringAsync(path));
        await app.WithDatabase(async db => Assert.Equal(2, await db.PostMedia.CountAsync(x => x.PostId == id)));
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Publish", new() { ["Input.Version"] = "0" })).StatusCode);
        var live = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
        Assert.StartsWith("gallery-", live.Slug); Assert.StartsWith("Gallery ·", live.DisplayTitle);
        Assert.Equal(2, live.Media.Length);
        Assert.Contains("A photo gallery", await client.GetStringAsync($"/posts/{live.Slug}/"));
    }

    private sealed class RenderFailure { public bool Fail { get; set; } }
    [Fact]
    public async Task Removing_a_guide_step_keeps_its_photo_in_the_introduction()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app, PostType.Guide);
        var image = await Photo(app, id); var step = Guid.NewGuid(); var fields = Fixture(PostType.Guide, 0, "Guide", image, step);
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Save", fields)).StatusCode);
        fields["Input.Version"] = "1"; fields["command"] = "remove-step:0";
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Arrange", fields)).StatusCode);
        await app.WithDatabase(async db =>
        {
            Assert.Empty(await db.GuideSteps.Where(x => x.PostId == id).ToArrayAsync());
            Assert.Null((await db.PostMedia.SingleAsync(x => x.PostId == id)).GuideStepId);
        });
        Assert.Contains(image.PrivateUrl, await client.GetStringAsync($"/admin/posts/{id}/preview"));
    }

    [Fact]
    public async Task Partial_batch_failure_preserves_saved_draft_and_reports_private_successes()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app, PostType.Gallery);
        var path = $"/admin/posts/{id}/edit";
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Save", new() { ["Input.Version"] = "0", ["Input.Title"] = "Saved gallery", ["Input.BodyMarkdown"] = "Saved introduction" })).StatusCode);
        var editor = await client.GetStringAsync(path);
        var token = WebUtility.HtmlDecode(Regex.Match(editor, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new MultipartFormDataContent(); form.Add(new StringContent(token), "__RequestVerificationToken");
        using var buffer = new MemoryStream(TestImages.Create());
        var valid = new ByteArrayContent(buffer.ToArray()); valid.Headers.ContentType = new("image/png"); form.Add(valid, "Uploads", "valid.png");
        var invalid = new ByteArrayContent([1, 2, 3]); invalid.Headers.ContentType = new("image/png"); form.Add(invalid, "Uploads", "invalid.png");
        var response = await client.PostAsync(path + "?handler=Upload", form); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Saved gallery", html); Assert.Contains("Saved introduction", html); Assert.Contains("1 earlier photo(s) were uploaded privately", html);
        await app.WithDatabase(async db => Assert.Single(await db.PostMedia.Where(x => x.PostId == id).ToArrayAsync()));
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    private sealed class FailingRenderer(IServiceProvider services, Microsoft.Extensions.Logging.ILoggerFactory logs, RenderFailure failure) : IStaticPageRenderer
    {
        public Task<string> RenderAsync(PublicPage page) => failure.Fail ? throw new IOException("Injected phase 4 build failure") : new StaticPageRenderer(services, logs).RenderAsync(page);
    }
}
