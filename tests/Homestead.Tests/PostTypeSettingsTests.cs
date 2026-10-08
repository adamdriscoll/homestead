using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class PostTypeSettingsTests
{
    private static Dictionary<string, string> Settings() => new()
    {
        ["Input.Title"] = "Homestead",
        ["Input.DisplayAuthor"] = "Ada",
        ["Input.CanonicalBaseUrl"] = "https://example.test",
        ["Input.TimeZone"] = "UTC",
        ["Input.AccentColor"] = "Porch"
    };

    private static async Task<ArticleSnapshot> Publish(TestApplication app, PostType type, string title)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync(type);
        using var bytes = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png")));
        var image = await scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(post.Id,
            new FormFile(bytes, 0, bytes.Length, "image", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "A photo");
        var input = new ArticleInput
        {
            Title = title,
            BodyMarkdown = $"Original content ![Photo]({image.Url})",
            Tags = title,
            Subject = title,
            Rating = 4,
            LocationName = "A place",
            Steps = [new() { BodyMarkdown = "Instructions" }]
        };
        var version = await articles.SaveAsync(post.Id, input);
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", version)).Succeeded);
        return app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single(x => x.PostId == post.Id);
    }

    [Theory]
    [InlineData(PostType.Microblog, "EnableNotes", "/notes/")]
    [InlineData(PostType.Article, "EnableArticles", "/articles/")]
    [InlineData(PostType.Review, "EnableReviews", "/reviews/")]
    [InlineData(PostType.Guide, "EnableGuides", "/guides/")]
    [InlineData(PostType.Gallery, "EnableGalleries", "/galleries/")]
    [InlineData(PostType.Checkin, "EnableCheckins", "/checkins/")]
    public async Task Disabled_types_are_hidden_retained_exported_privately_and_restored_after_restart(PostType type, string field, string listing)
    {
        using var data = new TemporaryData(); ArticleSnapshot original; string savedSnapshot; long draftVersion;
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        {
            _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
            original = await Publish(app, type, "Hidden example");
            var other = await Publish(app, type == PostType.Article ? PostType.Microblog : PostType.Article, "Visible example");
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
                var input = ArticleInput.From((await articles.FindAsync(original.PostId))!);
                input.BodyMarkdown = "Private pending edits";
                draftVersion = await articles.SaveAsync(original.PostId, input);
            }
            savedSnapshot = "";
            await app.WithDatabase(async db => savedSnapshot = (await db.PublishedSnapshots.SingleAsync(x => x.PostId == original.PostId)).SnapshotJson);
            var fields = Settings(); fields["Input." + field] = "false";
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, "/admin/settings", fields)).StatusCode);
            var active = app.Services.GetRequiredService<ActiveSite>().Current!;
            Assert.False(active.Settings.IsPostTypeEnabled(type));
            Assert.Single(active.Articles, x => x.PostId == other.PostId);
            Assert.DoesNotContain(active.Articles, x => x.PostId == original.PostId);
            foreach (var path in new[] { listing, $"/posts/{original.Slug}/", "/tags/hidden-example/", original.Media.Single().Url })
                Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(path)).StatusCode);
            foreach (var path in new[] { "/", "/rss.xml", "/sitemap.xml", "/admin", "/admin/posts", "/admin/posts?status=Published" })
            {
                var output = await author.GetStringAsync(path);
                Assert.DoesNotContain("Hidden example", output); Assert.DoesNotContain("hidden-example", output);
                Assert.DoesNotContain(original.PostId.ToString(), output);
                Assert.DoesNotContain($"href=\"{listing}\"", output);
            }
            var checkbox = await author.GetStringAsync("/admin/settings");
            Assert.Contains($"id=\"Input_{field}\"", checkbox);
            Assert.DoesNotContain($"value=\"{type}\"", await author.GetStringAsync("/admin/posts"));
            var create = await TestApplication.Submit(author, "/admin/posts", new() { ["type"] = type.ToString() }, "/admin/posts?handler=New");
            Assert.Equal("/admin/posts", create.Headers.Location!.ToString());
            var reserved = Guid.NewGuid(); var editor = $"/admin/posts/{reserved}/edit?newPostType={type}";
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(editor)).StatusCode);
            var save = await TestApplication.Submit(author, "/admin/posts", new()
            { ["NewPostType"] = type.ToString(), ["Input.Version"] = "0", ["Input.BodyMarkdown"] = "Stale new editor" }, $"/admin/posts/{reserved}/edit?handler=Save");
            Assert.Equal(HttpStatusCode.NotFound, save.StatusCode);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                Assert.False((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(original.PostId, "publish", draftVersion)).Succeeded);
                await using var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync();
                using var zip = new ZipArchive(archive);
                using var publishedReader = new StreamReader(zip.GetEntry($"content/posts/{original.Slug}/index.md")!.Open());
                var published = await publishedReader.ReadToEndAsync();
                Assert.Contains("\"draft\": true", published); Assert.Contains("\"homestead_type_disabled\": true", published);
                Assert.Contains("Original content", published); Assert.DoesNotContain("Private pending edits", published);
                using var workingReader = new StreamReader(zip.GetEntry($"content/posts/{original.Slug}-working-{original.PostId:N}/index.md")!.Open());
                Assert.Contains("Private pending edits", await workingReader.ReadToEndAsync());
                await using var staticArchive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
                using var staticZip = new ZipArchive(staticArchive);
                Assert.Null(staticZip.GetEntry($"posts/{original.Slug}/index.html"));
                Assert.Null(staticZip.GetEntry(original.Media.Single().Url.TrimStart('/')));
            }
            await app.WithDatabase(async db =>
            {
                var post = await db.Posts.Include(x => x.Draft).Include(x => x.PublishedSnapshot).Include(x => x.Media).SingleAsync(x => x.Id == original.PostId);
                Assert.Equal(PostStatus.Published, post.Status); Assert.True(post.PublishedSnapshot!.IsActive);
                Assert.Equal(savedSnapshot, post.PublishedSnapshot.SnapshotJson);
                Assert.Equal(draftVersion, post.Draft!.Version); Assert.Equal("Private pending edits", post.Draft.BodyMarkdown);
                Assert.Single(post.Media); Assert.False(await db.Posts.AnyAsync(x => x.Id == reserved));
            });
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var client = restarted.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/posts/{original.Slug}/")).StatusCode);
        await restarted.WithDatabase(async db => Assert.False((await db.SiteSettings.SingleAsync()).IsPostTypeEnabled(type)));
        _ = await TestApplication.Login(client);
        var enabled = Settings(); enabled["Input." + field] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", enabled)).StatusCode);
        var restored = restarted.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single(x => x.PostId == original.PostId);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
        var html = await client.GetStringAsync($"/posts/{original.Slug}/");
        Assert.Contains("Original content", html); Assert.DoesNotContain("Private pending edits", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(original.Media.Single().Url)).StatusCode);
        Assert.Contains("Hidden example", await client.GetStringAsync("/admin/posts"));
    }

    [Fact]
    public async Task Failed_disabling_keeps_active_output_until_retry_and_all_types_can_be_disabled()
    {
        using var data = new TemporaryData(); var fault = new ActivationFault();
        using var app = new TestApplication(data.Path, configureServices: services => services.AddSingleton<IGenerationActivation>(provider =>
            new FailingActivation(new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), fault)));
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var original = await Publish(app, PostType.Article, "Existing post");
        var active = app.Services.GetRequiredService<ActiveSite>(); var before = active.Current!.GenerationId;
        fault.Fail = true;
        var fields = Settings();
        foreach (var field in new[] { "EnableNotes", "EnableArticles", "EnableReviews", "EnableGuides", "EnableGalleries", "EnableCheckins" }) fields["Input." + field] = "false";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
        Assert.Contains("previous public site remains active", await client.GetStringAsync("/admin/settings"));
        Assert.Equal(before, active.Current!.GenerationId); Assert.True(active.Current.Settings.EnableArticles);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/posts/{original.Slug}/")).StatusCode);
        await app.WithDatabase(async db => Assert.Empty((await db.SiteSettings.SingleAsync()).EnabledPostTypes));
        var editor = await client.GetStringAsync($"/admin/posts/{original.PostId}/edit");
        Assert.Contains("previous post is still public", editor); Assert.Contains("This post type is disabled", editor);
        fault.Fail = false;
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/posts", new(), "/admin/posts?handler=Rebuild")).StatusCode);
        Assert.Empty(active.Current!.Articles); Assert.Empty(active.Current.Settings.EnabledPostTypes);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/posts/{original.Slug}/")).StatusCode);
        Assert.Contains("Enable a post type", await client.GetStringAsync("/admin"));
        Assert.Contains("Enable a post type", await client.GetStringAsync("/admin/posts"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/about/")).StatusCode);
        Assert.Contains("No posts here yet", await client.GetStringAsync("/"));
    }

    [Fact]
    public void Legacy_generation_settings_enable_every_type()
        => Assert.Equal(Enum.GetValues<PostType>(), JsonSerializer.Deserialize<SiteSettings>("{}")!.EnabledPostTypes);

    private sealed class ActivationFault { public bool Fail { get; set; } }
    private sealed class FailingActivation(GenerationActivation inner, ActivationFault fault) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fault.Fail ? throw new IOException("Injected activation failure") : inner.ActivateAsync(manifest);
    }
}
