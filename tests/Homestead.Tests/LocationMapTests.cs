using System.Net;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class LocationMapTests
{
    private static Task<HttpResponseMessage> SetMap(HttpClient client, bool enabled) => TestApplication.Submit(client, "/admin/settings", new()
    {
        ["Input.Title"] = "Homestead",
        ["Input.DisplayAuthor"] = "Ada",
        ["Input.CanonicalBaseUrl"] = "https://example.test",
        ["Input.TimeZone"] = "UTC",
        ["Input.EnableLocationMap"] = enabled.ToString()
    });

    [Fact]
    public async Task Map_is_opt_in_includes_all_public_types_and_uses_only_published_coordinates()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/map/")).StatusCode);
        Assert.DoesNotContain("href=\"/map/\"", await client.GetStringAsync("/"));
        Assert.Contains("id=\"Input_EnableLocationMap\"", await client.GetStringAsync("/admin/settings"));
        await PortabilityTests.Fixture(app);
        Assert.Equal(HttpStatusCode.Redirect, (await SetMap(client, true)).StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var active = app.Services.GetRequiredService<ActiveSite>();
        var original = active.Current!.Articles.First(x => x.Type == PostType.Article);
        var input = ArticleInput.From((await articles.FindAsync(original.PostId))!);
        input.LocationName = "PRIVATE draft place"; input.Latitude = -35; input.Longitude = 120;
        input.Version = await articles.SaveAsync(original.PostId, input);
        var nameOnly = await articles.CreateAsync(PostType.Checkin);
        await articles.SaveAsync(nameOnly.Id, new() { LocationName = "Name only place" });
        Assert.True((await publisher.TransitionAsync(nameOnly.Id, "publish", 1)).Succeeded);
        using var reader = app.Client(false);
        var html = await reader.GetStringAsync("/map/");
        foreach (var post in active.Current.Articles.Where(x => x.PostId != nameOnly.Id))
            Assert.Contains($"href=\"/posts/{post.Slug}/\"", html);
        Assert.Contains("data-latitude=\"41.88\" data-longitude=\"-87.63\"", html);
        Assert.DoesNotContain("PRIVATE", html); Assert.DoesNotContain("Name only place", html);
        Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;location&lt;/script&gt;", html);
        Assert.Contains("https://example.test/map/", await reader.GetStringAsync("/sitemap.xml"));
        Assert.Contains("href=\"/map/\"", await reader.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/assets/map/leaflet/leaflet.js")).StatusCode);
        using var mapResponse = await reader.GetAsync("/map/");
        Assert.Contains("https://tile.openstreetmap.org", mapResponse.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", mapResponse.Headers.GetValues("Referrer-Policy").Single());
        using var homeResponse = await reader.GetAsync("/");
        Assert.DoesNotContain("https://tile.openstreetmap.org", homeResponse.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("same-origin", homeResponse.Headers.GetValues("Referrer-Policy").Single());
        Assert.True((await publisher.TransitionAsync(original.PostId, "publish", input.Version)).Succeeded);
        html = await reader.GetStringAsync("/map/");
        Assert.Contains("data-latitude=\"-35\" data-longitude=\"120\"", html); Assert.Contains("PRIVATE draft place", html);
        input.LocationName = null; input.LocationAddress = null; input.Latitude = null; input.Longitude = null;
        input.Version = await articles.SaveAsync(original.PostId, input);
        Assert.True((await publisher.TransitionAsync(original.PostId, "publish", input.Version)).Succeeded);
        Assert.DoesNotContain($"href=\"/posts/{original.Slug}/\"", await reader.GetStringAsync("/map/"));
        var note = active.Current.Articles.Single(x => x.Type == PostType.Microblog);
        var noteVersion = (await articles.FindAsync(note.PostId))!.Draft!.Version;
        Assert.True((await publisher.TransitionAsync(note.PostId, "unpublish", noteVersion)).Succeeded);
        Assert.DoesNotContain($"href=\"/posts/{note.Slug}/\"", await reader.GetStringAsync("/map/"));
        var checkin = active.Current.Articles.Single(x => x.Type == PostType.Checkin && x.PostId != nameOnly.Id);
        Assert.True((await publisher.TransitionAsync(checkin.PostId, "delete", (await articles.FindAsync(checkin.PostId))!.Draft!.Version)).Succeeded);
        Assert.DoesNotContain($"href=\"/posts/{checkin.Slug}/\"", await reader.GetStringAsync("/map/"));
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).EnableReviews = false; await db.SaveChangesAsync(); });
        Assert.True((await publisher.RebuildAsync()).Succeeded);
        Assert.DoesNotContain("Fixture review", await reader.GetStringAsync("/map/"));
    }

    [Fact]
    public async Task Failed_disabling_preserves_active_map_and_static_export_until_retry_then_survives_restart()
    {
        using var data = new TemporaryData(); var fault = new ActivationFault();
        using (var app = new TestApplication(data.Path, configureServices: services => services.AddSingleton<IGenerationActivation>(provider =>
            new FailingActivation(new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), fault))))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
            Assert.Equal(HttpStatusCode.Redirect, (await SetMap(client, true)).StatusCode);
            Assert.Contains("No published posts with coordinates yet", await client.GetStringAsync("/map/"));
            var active = app.Services.GetRequiredService<ActiveSite>(); var before = active.Current!.GenerationId;
            fault.Fail = true;
            Assert.Equal(HttpStatusCode.Redirect, (await SetMap(client, false)).StatusCode);
            await app.WithDatabase(async db => Assert.False((await db.SiteSettings.SingleAsync()).EnableLocationMap));
            Assert.Equal(before, active.Current!.GenerationId); Assert.True(active.Current.Settings.EnableLocationMap);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/map/")).StatusCode);
            await using var scope = app.Services.CreateAsyncScope();
            await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
            {
                using var zip = new System.IO.Compression.ZipArchive(archive);
                Assert.NotNull(zip.GetEntry("map/index.html")); Assert.NotNull(zip.GetEntry("assets/map/leaflet/leaflet.js"));
            }
            fault.Fail = false;
            Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
            Assert.False(active.Current!.Settings.EnableLocationMap);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/map/")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/map/leaflet/leaflet.js")).StatusCode);
            Assert.DoesNotContain("/map/", await client.GetStringAsync("/sitemap.xml"));
            Assert.DoesNotContain("href=\"/map/\"", await client.GetStringAsync("/"));
            await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
            {
                using var zip = new System.IO.Compression.ZipArchive(archive);
                Assert.Null(zip.GetEntry("content/map/index.md")); Assert.Null(zip.GetEntry("static/assets/map/leaflet/leaflet.js"));
            }
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var reader = restarted.Client(false);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/map/")).StatusCode);
        await restarted.WithDatabase(async db => Assert.False((await db.SiteSettings.SingleAsync()).EnableLocationMap));
    }

    [Fact]
    public void Legacy_settings_leave_location_map_disabled() => Assert.False(JsonSerializer.Deserialize<SiteSettings>("{}")!.EnableLocationMap);

    private sealed class ActivationFault { public bool Fail { get; set; } }
    private sealed class FailingActivation(GenerationActivation inner, ActivationFault fault) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fault.Fail ? throw new IOException("Injected activation failure") : inner.ActivateAsync(manifest);
    }
}
