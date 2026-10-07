using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class PublishingTests
{
    private static async Task<Guid> Create(HttpClient client)
    {
        var response = await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=New");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Guid.Parse(response.Headers.Location!.ToString().Split('/')[3]);
    }

    private static Dictionary<string, string> Fields(long version, string title = "A weekend at home", string body = "# Morning\n\nAn **original** story.") => new()
    {
        ["Input.Title"] = title,
        ["Input.BodyMarkdown"] = body,
        ["Input.Version"] = version.ToString(),
        ["Input.Summary"] = "Our first story",
        ["Input.Tags"] = "Home, Life"
    };
    private static async Task<HttpResponseMessage> Action(HttpClient client, Guid id, string handler, Dictionary<string, string> fields)
        => await TestApplication.Submit(client, $"/admin/posts/{id}/edit", fields, $"/admin/posts/{id}/edit?handler={handler}");

    private static async Task<string> Token(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid id, string name, byte[] bytes, string mime)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(await Token(client, $"/admin/posts/{id}/edit")), "__RequestVerificationToken");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new(mime);
        form.Add(file, "Upload", name); form.Add(new StringContent("The morning garden"), "UploadAlt");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/posts/{id}/edit?handler=Upload") { Content = form };
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request);
        Assert.True(response.StatusCode != HttpStatusCode.InternalServerError, await response.Content.ReadAsStringAsync());
        return response;
    }

    private static byte[] Photo()
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(2, 2);
        using var stream = new MemoryStream(); image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder()); return stream.ToArray();
    }

    [Fact]
    public async Task Article_with_photo_lifecycle_keeps_drafts_private_and_recovers_after_restart()
    {
        using var data = new TemporaryData(); Guid id; string publicPhoto; byte[] photo = Photo();
        var url = "/posts/a-weekend-at-home/";
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        using (var visitor = app.Client())
        {
            _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
            Assert.Contains("Stories will appear here soon", await visitor.GetStringAsync("/"));
            id = await Create(author);
            var uploaded = await Upload(author, id, "garden.png", photo, "image/png");
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            using var metadata = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            publicPhoto = metadata.RootElement.GetProperty("url").GetString()!;
            var privatePhoto = metadata.RootElement.GetProperty("privateUrl").GetString()!;
            var mediaId = metadata.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync(privatePhoto)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(publicPhoto)).StatusCode);
            var body = "# Morning\n\nAn **original** story.\n\n![Garden](" + publicPhoto + ")\n\n- First\n- Second\n\n```csharp\nvar x = 1;\n```";
            var fields = Fields(0, body: body); fields["Input.CoverMediaId"] = mediaId;
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Save", fields)).StatusCode);
            var preview = await author.GetAsync($"/admin/posts/{id}/preview");
            Assert.Equal("no-store", preview.Headers.CacheControl!.ToString());
            Assert.Contains(privatePhoto, await preview.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/admin/posts/{id}/preview")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Publish", Fields(1, body: body))).StatusCode);
            var original = await visitor.GetStringAsync(url);
            Assert.Contains("<strong>original</strong>", original); Assert.Contains("<ul>", original); Assert.Contains("<pre>", original);
            Assert.Contains("https://example.test/posts/a-weekend-at-home/", original);
            Assert.DoesNotContain("/admin/media", original); Assert.Equal(photo, await visitor.GetByteArrayAsync(publicPhoto));
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Save", Fields(2, body: "Private edited draft"))).StatusCode);
            Assert.Equal(original, await visitor.GetStringAsync(url));
            Assert.Contains("Private edited draft", await author.GetStringAsync($"/admin/posts/{id}/preview"));
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Publish", Fields(3, body: "Updated article"))).StatusCode);
            Assert.Contains("Updated article", await visitor.GetStringAsync(url));
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(publicPhoto)).StatusCode); // Orphan upload is absent from the generation.
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Unpublish", Fields(4))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Publish", Fields(4, "Changed title", body))).StatusCode);
            Assert.Contains("Changed title", await visitor.GetStringAsync(url)); // First slug stays locked.
        }
        using (var restarted = new TestApplication(data.Path, setupToken: null))
        using (var author = restarted.Client())
        using (var visitor = restarted.Client())
        {
            Assert.Contains("Changed title", await visitor.GetStringAsync(url));
            Assert.Equal(photo, await visitor.GetByteArrayAsync(publicPhoto));
            _ = await TestApplication.Login(author);
            Assert.Contains("Changed title", await author.GetStringAsync($"/admin/posts/{id}/edit"));
            var fields = Fields(5); fields["ConfirmDelete"] = "true";
            Assert.Equal(HttpStatusCode.Redirect, (await Action(author, id, "Delete", fields)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(publicPhoto)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"/admin/posts/{id}/edit")).StatusCode);
        }
    }

    [Fact]
    public async Task Incomplete_drafts_save_stale_tabs_conflict_and_invalid_publication_stays_private()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(client);
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Save", Fields(0, "", ""))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Action(client, id, "Save", Fields(0, "Stale", "Lost edits"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, "Publish", Fields(1, "", ""))).StatusCode);
        Assert.Contains("needs a title and body", await client.GetStringAsync($"/admin/posts/{id}/edit"));
        await app.WithDatabase(async db =>
        { Assert.Equal(PostStatus.Draft, (await db.Posts.SingleAsync()).Status); Assert.Empty(await db.PublishedSnapshots.ToListAsync()); });
        Assert.Equal(HttpStatusCode.BadRequest, (await Action(client, id, "Delete", Fields(2))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/admin/posts/{id}/edit?handler=Save", new FormUrlEncodedContent(Fields(2)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/admin/posts/{id}/edit?handler=Upload", new MultipartFormDataContent())).StatusCode);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync($"/admin/posts/{id}/edit")).StatusCode);
    }

    [Theory]
    [InlineData("photo.heic", "image/heic", "HEIC/HEIF")]
    [InlineData("photo.heif", "image/heif", "HEIC/HEIF")]
    [InlineData("photo.svg", "image/svg+xml", "JPEG, PNG, or WebP")]
    [InlineData("../photo.png", "image/png", "without a path")]
    [InlineData("photo.png", "image/png", "damaged or unsupported")]
    [InlineData("photo.jpg", "image/jpeg", "must match")]
    public async Task Unsafe_unsupported_and_spoofed_uploads_are_rejected_without_media_records(string filename, string mime, string message)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(client);
        var bytes = filename == "photo.jpg" ? Photo() : "not an image"u8.ToArray();
        var result = await Upload(client, id, filename, bytes, mime);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode); Assert.Contains(message, await result.Content.ReadAsStringAsync());
        await app.WithDatabase(async db => Assert.Empty(await db.MediaAssets.ToListAsync()));
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "media", "originals"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("unpublish")]
    [InlineData("delete")]
    [InlineData("publish")]
    public async Task Failed_generation_preserves_old_site_discloses_active_state_and_rebuild_retries_desired_changes(string action)
    {
        using var data = new TemporaryData(); var failure = new RenderFailure(); Guid id;
        using (var app = new TestApplication(data.Path, configureServices: services =>
        {
            services.AddSingleton(failure);
            services.AddScoped<IStaticPageRenderer, FailingRenderer>();
        }))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); id = await Create(client);
            _ = await Action(client, id, "Publish", Fields(0));
            var original = await client.GetStringAsync("/posts/a-weekend-at-home/");
            var pointer = await File.ReadAllTextAsync(Path.Combine(data.Path, "site", "current.json"));
            failure.Fail = true;
            var fields = Fields(1, body: "Desired newer article"); fields["ConfirmDelete"] = "true";
            Assert.Equal(HttpStatusCode.Redirect, (await Action(client, id, action, fields)).StatusCode);
            Assert.Equal(pointer, await File.ReadAllTextAsync(Path.Combine(data.Path, "site", "current.json")));
            Assert.Equal(original, await client.GetStringAsync("/posts/a-weekend-at-home/"));
            var status = await client.GetStringAsync(action == "delete" ? "/admin/posts" : $"/admin/posts/{id}/edit");
            Assert.Contains("still public", status);
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var author = restarted.Client();
        Assert.Contains("original", await author.GetStringAsync("/posts/a-weekend-at-home/"));
        _ = await TestApplication.Login(author);
        // Publishing an unrelated article applies ALL saved desired changes, including failed removals.
        var other = await Create(author); _ = await Action(author, other, "Publish", Fields(0, "Another article", "Other body"));
        if (action == "publish") Assert.Contains("Desired newer article", await author.GetStringAsync("/posts/a-weekend-at-home/"));
        else Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync("/posts/a-weekend-at-home/")).StatusCode);
    }

    [Fact]
    public async Task Missing_original_during_build_keeps_active_image_and_pointer_unchanged()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(client);
        var uploaded = await Upload(client, id, "garden.png", Photo(), "image/png");
        using var metadata = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()); var url = metadata.RootElement.GetProperty("url").GetString()!;
        _ = await Action(client, id, "Publish", Fields(0, body: $"Original photo\n\n![Garden]({url})"));
        var pointer = File.ReadAllText(Path.Combine(data.Path, "site", "current.json"));
        File.Delete(Directory.GetFiles(Path.Combine(data.Path, "media", "originals"), "*", SearchOption.AllDirectories).Single());
        _ = await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=Rebuild");
        Assert.Equal(pointer, File.ReadAllText(Path.Combine(data.Path, "site", "current.json")));
        Assert.Equal(Photo(), await client.GetByteArrayAsync(url));
        Assert.Contains("generation failed", await client.GetStringAsync("/admin/posts"));
    }

    [Theory]
    [InlineData("jpg", "image/jpeg")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("webp", "image/webp")]
    public async Task Supported_images_decode_and_record_dimensions(string extension, string mime)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(client);
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(3, 4);
        using var bytes = new MemoryStream();
        SixLabors.ImageSharp.Formats.IImageEncoder encoder = extension == "webp" ? new SixLabors.ImageSharp.Formats.Webp.WebpEncoder() : new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder();
        image.Save(bytes, encoder);
        Assert.Equal(HttpStatusCode.OK, (await Upload(client, id, "phone." + extension, bytes.ToArray(), mime)).StatusCode);
        await app.WithDatabase(async db =>
        {
            var asset = await db.MediaAssets.SingleAsync(); Assert.Equal(3, asset.Width); Assert.Equal(4, asset.Height); Assert.Equal(mime, asset.MimeType);
        });
    }

    [Fact]
    public async Task Oversized_and_truncated_images_do_not_create_originals()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(client);
        var oversized = await Upload(client, id, "large.png", new byte[20 * 1024 * 1024 + 1], "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode); Assert.Contains("20 MB", await oversized.Content.ReadAsStringAsync());
        var truncated = await Upload(client, id, "broken.png", Photo()[..32], "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);
        await app.WithDatabase(async db => Assert.Empty(await db.MediaAssets.ToListAsync()));
    }

    [Fact]
    public async Task Restart_reconciles_an_interrupted_build_record_after_pointer_activation()
    {
        using var data = new TemporaryData(); Guid generation;
        using (var app = new TestApplication(data.Path))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); generation = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
            await app.WithDatabase(async db => { var build = await db.SiteGenerations.SingleAsync(x => x.Id == generation); build.Status = GenerationStatus.Building; build.ActivatedAtUtc = null; await db.SaveChangesAsync(); });
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var visitor = restarted.Client();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/")).StatusCode);
        await restarted.WithDatabase(async db => { var build = await db.SiteGenerations.SingleAsync(x => x.Id == generation); Assert.Equal(GenerationStatus.Succeeded, build.Status); Assert.NotNull(build.ActivatedAtUtc); });
    }

    [Fact]
    public async Task Concurrent_publish_commands_serialize_and_slugs_do_not_collide()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var first = await Create(client); var second = await Create(client);
        _ = await Action(client, first, "Save", Fields(0)); _ = await Action(client, second, "Save", Fields(0));
        async Task Publish(Guid id)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(id, "publish", 1);
            Assert.True(result.Succeeded, result.Message);
        }
        await Task.WhenAll(Publish(first), Publish(second));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/posts/a-weekend-at-home/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/posts/a-weekend-at-home-2/")).StatusCode);
        Assert.Equal(2, app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Length);
    }

    [Fact]
    public async Task Public_serving_does_not_query_the_database_and_only_exposes_manifest_files()
    {
        using var data = new TemporaryData(); var guard = new DatabaseGuard();
        using var app = new TestApplication(data.Path, configureServices: services =>
            services.AddDbContext<HomesteadDbContext>((_, options) => options.AddInterceptors(guard)));
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var id = await Create(client); _ = await Action(client, id, "Publish", Fields(0)); guard.Block = true;
        Assert.Contains("original", await client.GetStringAsync("/posts/a-weekend-at-home/"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/assets/site.css")).StatusCode);
        foreach (var path in new[] { "/homestead.db", "/site/current.json", "/keys/key.xml", "/media/originals/image.png", "/assets/../current.json", "/posts/%2e%2e/current.json", "/assets/site.css%5c..%5cindex.html" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Corrupt_active_generation_fails_readiness_and_admin_can_rebuild()
    {
        using var data = new TemporaryData();
        using (var app = new TestApplication(data.Path))
        using (var client = app.Client()) { _ = await TestApplication.Setup(client); }
        var manifest = JsonSerializer.Deserialize<GenerationManifest>(File.ReadAllText(Path.Combine(data.Path, "site", "current.json")))!;
        File.Delete(Path.Combine(data.Path, "site", "builds", manifest.GenerationId.ToString("N"), "index.html"));
        using var restarted = new TestApplication(data.Path, setupToken: null); using var author = restarted.Client();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await author.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync("/")).StatusCode);
        _ = await TestApplication.Login(author); _ = await TestApplication.Submit(author, "/admin/posts", [], "/admin/posts?handler=Rebuild");
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("broken JSON")]
    public async Task Invalid_active_manifest_keeps_admin_available_for_rebuild(string manifest)
    {
        using var data = new TemporaryData();
        using (var app = new TestApplication(data.Path))
        using (var client = app.Client()) { _ = await TestApplication.Setup(client); }
        await File.WriteAllTextAsync(Path.Combine(data.Path, "site", "current.json"), manifest);
        using var restarted = new TestApplication(data.Path, setupToken: null); using var author = restarted.Client();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await author.GetAsync("/health/ready")).StatusCode);
        _ = await TestApplication.Login(author);
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/admin/posts")).StatusCode);
        _ = await TestApplication.Submit(author, "/admin/posts", [], "/admin/posts?handler=Rebuild");
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/health/ready")).StatusCode);
    }

    private sealed class RenderFailure { public bool Fail { get; set; } }
    private sealed class FailingRenderer(IServiceProvider services, Microsoft.Extensions.Logging.ILoggerFactory logs, RenderFailure failure) : IStaticPageRenderer
    {
        public Task<string> RenderAsync(PublicPage page) => failure.Fail ? throw new IOException("Injected render/disk failure") : new StaticPageRenderer(services, logs).RenderAsync(page);
    }
    private sealed class DatabaseGuard : DbCommandInterceptor
    {
        public bool Block { get; set; }
        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
            => Block ? throw new InvalidOperationException("Public request queried SQLite") : result;
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
            => Block ? throw new InvalidOperationException("Public request queried SQLite") : ValueTask.FromResult(result);
    }
}
