using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Features.Settings;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class FitnessTests
{
    [Fact]
    public async Task Private_map_assets_are_authenticated_and_available_before_any_route_is_published()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin/maps/fitness-map.js")).StatusCode);
        _ = await TestApplication.Login(client);
        foreach (var file in new[] { "fitness-map.js", "leaflet/leaflet.js", "leaflet/leaflet.css" })
        {
            var response = await client.GetAsync("/admin/maps/" + file);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/map/" + file)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/maps/Program.cs")).StatusCode);
        await using var scope = app.Services.CreateAsyncScope(); var id = Guid.NewGuid();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().ImportGpxAsync(id, 0, Upload(), PostType.Fitness);
        var preview = await client.GetAsync($"/admin/posts/{id}/preview");
        Assert.True(preview.Headers.CacheControl!.NoStore);
        Assert.Contains("script-src 'self'", preview.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'self'", preview.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", preview.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("Show route map", await preview.Content.ReadAsStringAsync());
        Assert.DoesNotContain("/assets/map/fitness-map.js", await client.GetStringAsync("/"));
    }

    [Theory]
    [InlineData(FitnessUnitSystem.Metric, "1.61 km", "3:43 /km", "16.09 km/h", "30.48 m")]
    [InlineData(FitnessUnitSystem.Imperial, "1.00 mi", "6:00 /mi", "10 mi/h", "100 ft")]
    public void Display_units_convert_measurements_without_changing_stored_values(FitnessUnitSystem units, string distance, string pace, string speed, string elevation)
    {
        var activity = new FitnessSnapshot([], 1609.344, 360, 30.48, null, 130, 140);
        Assert.Equal(distance, activity.FormatDistance(units)); Assert.Equal(pace, activity.FormatPace(units));
        var html = FitnessRendering.Summary(activity, units);
        Assert.Contains(speed, html); Assert.Contains(elevation, html); Assert.Contains("130 bpm", html);
        Assert.Equal(1609.344, activity.DistanceMeters); Assert.Equal(30.48, activity.ElevationGainMeters);
    }

    [Fact]
    public async Task Unit_settings_validate_persist_and_keep_failed_rebuilds_out_of_the_active_site()
    {
        using var data = new TemporaryData(); var fault = new RenderFault();
        using (var app = new TestApplication(data.Path, configureServices: services => services.AddScoped<IStaticPageRenderer>(sp =>
            new FaultyRenderer(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), fault))))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
            await using var scope = app.Services.CreateAsyncScope();
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
            var id = Guid.NewGuid(); var version = await articles.ImportGpxAsync(id, 0, Upload(), PostType.Fitness);
            Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
            var active = app.Services.GetRequiredService<ActiveSite>(); var post = active.Current!.Articles.Single();
            var fields = new Dictionary<string, string>
            {
                ["Input.Title"] = "Homestead",
                ["Input.CanonicalBaseUrl"] = "https://example.test",
                ["Input.DisplayAuthor"] = "Ada",
                ["Input.TimeZone"] = "UTC",
                ["Input.AccentColor"] = "Porch",
                ["Input.ThemeId"] = "porch",
                ["Input.FitnessUnits"] = "99"
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/settings", new FormUrlEncodedContent(fields))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
            Assert.Equal(FitnessUnitSystem.Metric, active.Current.Settings.FitnessUnits);
            fields["Input.FitnessUnits"] = "Imperial"; fault.Fail = true;
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
            await app.WithDatabase(async db => Assert.Equal(FitnessUnitSystem.Imperial, (await db.SiteSettings.SingleAsync()).FitnessUnits));
            Assert.Equal(FitnessUnitSystem.Metric, active.Current.Settings.FitnessUnits);
            Assert.Contains("Speed (km/h)", await client.GetStringAsync($"/posts/{post.Slug}/"));
            fault.Fail = false;
            Assert.Contains("Speed (mi/h)", await client.GetStringAsync($"/admin/posts/{id}/preview"));
            Assert.Contains("Speed (mi/h)", await client.GetStringAsync($"/admin/posts/{id}/edit"));
            await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
            {
                using var zip = new ZipArchive(archive); using var reader = new StreamReader(zip.GetEntry($"posts/{post.Slug}/index.html")!.Open());
                Assert.Contains("Speed (km/h)", await reader.ReadToEndAsync());
            }
            Assert.True((await publishing.RebuildAsync()).Succeeded);
            Assert.Contains("Speed (mi/h)", await client.GetStringAsync($"/posts/{post.Slug}/"));
            var rss = await client.GetStringAsync("/rss.xml");
            Assert.Contains("Speed (mi/h)", rss); Assert.Contains(post.Activity!.FormatDistance(FitnessUnitSystem.Imperial), rss);
            Assert.Equal(post.Activity.DistanceMeters, active.Current!.Articles.Single().Activity!.DistanceMeters);
            var legacy = JsonSerializer.Deserialize<SiteSettings>("{}")!;
            Assert.Equal(FitnessUnitSystem.Metric, legacy.FitnessUnits);
            Assert.Equal(FitnessUnitSystem.Imperial, SettingsInput.From(active.Current.Settings).FitnessUnits);
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var visitor = restarted.Client();
        var restartedSite = restarted.Services.GetRequiredService<ActiveSite>().Current!;
        Assert.Equal(FitnessUnitSystem.Imperial, restartedSite.Settings.FitnessUnits);
        Assert.Contains("Speed (mi/h)", await visitor.GetStringAsync($"/posts/{restartedSite.Articles.Single().Slug}/"));
    }

    internal const string Gpx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gpx xmlns="http://www.topografix.com/GPX/1/1" xmlns:gpxtpx="http://www.garmin.com/xmlschemas/TrackPointExtension/v1" version="1.1" creator="fixture">
          <metadata><name>PRIVATE GPS METADATA</name><author><name>PRIVATE AUTHOR</name></author></metadata>
          <trk><name>PRIVATE TRACK NAME</name><trkseg>
            <trkpt lat="0" lon="0"><ele>10</ele><time>2026-10-11T12:00:00Z</time><extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>120</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>
            <trkpt lat="0" lon="0.001"><ele>20</ele><time>2026-10-11T12:01:00Z</time><extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>140</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>
            <trkpt lat="0" lon="0.002"><ele>15</ele><time>2026-10-11T12:02:00Z</time></trkpt>
          </trkseg></trk>
        </gpx>
        """;

    private static FormFile Upload(string text = Gpx, string name = "activity.gpx")
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new(new MemoryStream(bytes), 0, bytes.Length, "GpxUpload", name) { Headers = new HeaderDictionary(), ContentType = "application/gpx+xml" };
    }

    [Fact]
    public void Track_metrics_use_timestamps_extensions_and_segment_boundaries()
    {
        var track = GpxParser.Parse(Encoding.UTF8.GetBytes(Gpx));
        Assert.InRange(track.DistanceMeters, 222.3, 222.5); Assert.Equal(120, track.DurationSeconds);
        Assert.Equal(10, track.ElevationGainMeters); Assert.Equal(130, track.AverageHeartRate); Assert.Equal(140, track.MaximumHeartRate);
        Assert.Equal(DateTimeKind.Utc, track.StartedAtUtc!.Value.Kind);
        Assert.Null(track.Segments[0].Points[0].SpeedMetersPerSecond);
        Assert.InRange(track.Segments[0].Points[1].SpeedMetersPerSecond!.Value, 1.85, 1.86);
        var split = Gpx.Replace("<trkpt lat=\"0\" lon=\"0.002\">", "</trkseg><trkseg><trkpt lat=\"0\" lon=\"1\">");
        var segmented = GpxParser.Parse(Encoding.UTF8.GetBytes(split));
        Assert.InRange(segmented.DistanceMeters, 111.1, 111.3); Assert.Equal(60, segmented.DurationSeconds);
        Assert.Null(segmented.Segments[1].Points[0].SpeedMetersPerSecond);
        var noTimes = Regex.Replace(Gpx, "<time>.*?</time>", "");
        var untimed = GpxParser.Parse(Encoding.UTF8.GetBytes(noTimes));
        Assert.Null(untimed.DurationSeconds); Assert.Null(untimed.PaceText);
        Assert.Contains("timestamps unavailable", FitnessRendering.Render(untimed));
        var legacy = Gpx.Replace("GPX/1/1", "GPX/1/0").Replace("version=\"1.1\"", "version=\"1.0\"").Replace("<ele>20</ele>", "<ele>20</ele><speed>3</speed>");
        Assert.Equal(3, GpxParser.Parse(Encoding.UTF8.GetBytes(legacy)).Segments[0].Points[1].SpeedMetersPerSecond);
        var v2 = Gpx.Replace("TrackPointExtension/v1", "TrackPointExtension/v2").Replace("<gpxtpx:hr>140</gpxtpx:hr>", "<gpxtpx:hr>140</gpxtpx:hr><gpxtpx:speed>2.5</gpxtpx:speed>");
        Assert.Equal(2.5, GpxParser.Parse(Encoding.UTF8.GetBytes(v2)).Segments[0].Points[1].SpeedMetersPerSecond);
    }

    [Theory]
    [InlineData("coordinates")]
    [InlineData("nonfinite")]
    [InlineData("time")]
    [InlineData("backwards")]
    [InlineData("heart")]
    [InlineData("dtd")]
    [InlineData("routes")]
    [InlineData("points")]
    [InlineData("elevation")]
    [InlineData("speed")]
    [InlineData("depth")]
    [InlineData("segments")]
    public void Invalid_or_oversized_tracks_are_rejected(string reason)
    {
        var text = reason switch
        {
            "coordinates" => Gpx.Replace("lat=\"0\"", "lat=\"91\""),
            "nonfinite" => Gpx.Replace("lon=\"0\"", "lon=\"NaN\""),
            "time" => Gpx.Replace("12:00:00Z", "12:00:00"),
            "backwards" => Gpx.Replace("12:01:00Z", "11:59:00Z"),
            "heart" => Gpx.Replace(">140<", ">140.5<"),
            "dtd" => "<!DOCTYPE gpx [<!ENTITY leak SYSTEM 'file:///private'>]>" + Gpx[(Gpx.IndexOf("<gpx", StringComparison.Ordinal))..],
            "routes" => "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\" version=\"1.1\"><rte><rtept lat=\"0\" lon=\"0\"/></rte></gpx>",
            "elevation" => Gpx.Replace("<ele>20</ele>", "<ele>1e308</ele>"),
            "speed" => Gpx.Replace("TrackPointExtension/v1", "TrackPointExtension/v2").Replace("<gpxtpx:hr>140</gpxtpx:hr>", "<gpxtpx:speed>1e308</gpxtpx:speed>"),
            "depth" => "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\" version=\"1.1\">" + string.Concat(Enumerable.Repeat("<extensions>", 34)) + string.Concat(Enumerable.Repeat("</extensions>", 34)) + "</gpx>",
            "segments" => "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\" version=\"1.1\"><trk>" + string.Concat(Enumerable.Repeat("<trkseg><trkpt lat=\"0\" lon=\"0\"/><trkpt lat=\"0\" lon=\"1\"/></trkseg>", 101)) + "</trk></gpx>",
            _ => "<gpx xmlns=\"http://www.topografix.com/GPX/1/1\" version=\"1.1\"><trk><trkseg>" + string.Concat(Enumerable.Repeat("<trkpt lat=\"0\" lon=\"0\"/>", GpxParser.MaximumPoints + 1)) + "</trkseg></trk></gpx>"
        };
        Assert.Throws<InvalidOperationException>(() => GpxParser.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Old_snapshots_omit_activity_and_oversized_files_are_rejected()
    {
        var snapshot = new ArticleSnapshot(1, Guid.NewGuid(), "old", "Old", "Body", null, DateTime.UtcNow, DateTime.UtcNow, 0, [], null, []);
        var json = JsonSerializer.SerializeToNode(snapshot)!; json.AsObject().Remove("Activity");
        Assert.Null(JsonSerializer.Deserialize<ArticleSnapshot>(json.ToJsonString())!.Activity);
        Assert.Throws<InvalidOperationException>(() => GpxParser.Parse(new byte[GpxParser.MaximumBytes + 1]));
        var withoutSensors = Regex.Replace(Gpx, "<extensions>.*?</extensions>", "");
        var activity = GpxParser.Parse(Encoding.UTF8.GetBytes(withoutSensors));
        Assert.Null(activity.AverageHeartRate); Assert.Null(activity.MaximumHeartRate);
        Assert.DoesNotContain("Heart rate (bpm)", FitnessRendering.Render(activity));
        Assert.DoesNotContain("data-fitness-tab=\"heart-rate\"", FitnessRendering.Render(activity));
    }

    [Fact]
    public async Task Import_is_authenticated_antiforgery_protected_and_versioned_including_new_drafts()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        var id = Guid.NewGuid(); var path = $"/admin/posts/{id}/edit?newPostType=Fitness";
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(path + "&handler=Gpx", new FormUrlEncodedContent([]))).StatusCode);
        _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path + "&handler=Gpx", new FormUrlEncodedContent([]))).StatusCode);
        await using var scope = app.Services.CreateAsyncScope(); var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.ImportGpxAsync(id, 0, Upload("bad"), PostType.Fitness));
        Assert.Null(await articles.FindAsync(id));
        var html = await client.GetStringAsync(path);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken"); form.Add(new StringContent("0"), "Input.Version");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Gpx)), "GpxUpload", "track.gpx");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(path + "&handler=Gpx", form)).StatusCode);
        var saved = (await articles.FindAsync(id))!;
        Assert.Equal(1, saved.Draft!.Version); Assert.NotNull(saved.Activity);
        await Assert.ThrowsAsync<DraftConflictException>(() => articles.ImportGpxAsync(id, 0, Upload()));
        using var anonymous = app.Client(false);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync($"/admin/posts/{id}/edit?handler=Gpx")).StatusCode);
        var original = await client.GetAsync($"/admin/posts/{id}/edit?handler=Gpx");
        Assert.Equal(Gpx, await original.Content.ReadAsStringAsync()); Assert.Equal("no-store", original.Headers.CacheControl!.ToString());
        var nonActivity = await articles.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.ImportGpxAsync(nonActivity.Id, 0, Upload()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => articles.ImportGpxAsync(id, 1, Upload(name: "track.xml")));
    }

    [Fact]
    public async Task Every_theme_supports_snapshots_photos_private_previews_and_atomic_track_updates()
    {
        using var data = new TemporaryData(); var fault = new RenderFault();
        using var app = new TestApplication(data.Path, configureServices: services => services.AddScoped<IStaticPageRenderer>(sp =>
            new FaultyRenderer(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), fault)));
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>(); var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var id = Guid.NewGuid(); var version = await articles.ImportGpxAsync(id, 0, Upload(), PostType.Fitness);
        using var imageBytes = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png")));
        var photo = await scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(id,
            new FormFile(imageBytes, 0, imageBytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "A finish-line photo");
        var input = ArticleInput.From((await articles.FindAsync(id))!); input.Title = "Morning run"; input.BodyMarkdown = "A quiet run."; input.Tags = "Fitness";
        version = await articles.SaveAsync(id, input); Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>(); var live = active.Current!.Articles.Single();
        foreach (var theme in ThemeService.BuiltInIds)
        {
            foreach (var units in new[] { FitnessUnitSystem.Imperial, FitnessUnitSystem.Metric })
            {
                await app.WithDatabase(async db => { var settings = await db.SiteSettings.SingleAsync(); settings.ThemeId = theme; settings.FitnessUnits = units; await db.SaveChangesAsync(); });
                Assert.True((await publishing.RebuildAsync()).Succeeded);
                foreach (var route in new[] { "/", "/fitness/", $"/tags/{live.TagSlug("Fitness")}/", $"/posts/{live.Slug}/" })
                {
                    var html = await client.GetStringAsync(route);
                    Assert.Contains("data-fitness-summary", html); Assert.DoesNotContain("PRIVATE", html);
                    Assert.Contains("Morning run", html);
                    Assert.Contains(live.Activity!.FormatDistance(units), html);
                }
                var details = await client.GetStringAsync($"/posts/{live.Slug}/");
                Assert.Contains("data-fitness-route", details); Assert.Contains("Heart rate (bpm)", details); Assert.Contains(photo.Url, details);
                Assert.Contains("/assets/map/fitness-map.js", details); Assert.Contains("data-private-map=\"false\"", details);
                Assert.Contains("data-fitness-map-toolbar", details); Assert.Contains("aria-label=\"Fit route\"", details);
                Assert.DoesNotContain("data-fitness-diagram", details);
                Assert.Contains("data-fitness-tabs", details); Assert.Contains("data-fitness-tab=\"heart-rate\"", details);
                foreach (var text in new[] { "Recorded route", "Separate track segments are not joined.", "Diagrams are sampled for display.", "Distance follows recorded GPS points." })
                    Assert.DoesNotContain(text, details);
                var preview = await client.GetStringAsync($"/admin/posts/{id}/preview");
                Assert.Contains("data-fitness-route", preview); Assert.Contains(photo.PrivateUrl, preview);
                Assert.Contains("/admin/maps/fitness-map.js", preview); Assert.Contains("data-private-map=\"true\"", preview);
                Assert.DoesNotContain("data-fitness-diagram", preview); Assert.Contains("data-fitness-map-toolbar", preview);
                Assert.Contains($"Speed ({FitnessSnapshot.DistanceUnit(units)}/h)", details);
                Assert.Contains($"Speed ({FitnessSnapshot.DistanceUnit(units)}/h)", preview);
            }
        }
        version = await articles.ImportGpxAsync(id, version, Upload(Gpx.Replace("lon=\"0.002\"", "lon=\"0.004\"")));
        Assert.True(ArticleService.HasDraftChanges((await articles.FindAsync(id))!, active.Current!.Articles.Single()));
        Assert.Equal(live.Activity!.DistanceMeters, active.Current.Articles.Single().Activity!.DistanceMeters);
        fault.Fail = true; Assert.False((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
        Assert.True(ArticleService.IsPublicationPending((await articles.FindAsync(id))!, active.Current.Articles.Single()));
        Assert.Equal(live.Activity.DistanceMeters, active.Current.Articles.Single().Activity!.DistanceMeters);
        await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
        {
            using var zip = new ZipArchive(archive); using var reader = new StreamReader(zip.GetEntry($"posts/{live.Slug}/index.html")!.Open());
            Assert.Contains(live.Activity.DistanceText, await reader.ReadToEndAsync()); Assert.DoesNotContain(zip.Entries, x => x.FullName.EndsWith(".gpx"));
            Assert.NotNull(zip.GetEntry("assets/map/fitness-map.js")); Assert.NotNull(zip.GetEntry("assets/map/leaflet/leaflet.css"));
        }
        fault.Fail = false; Assert.True((await publishing.RebuildAsync()).Succeeded);
        var updated = active.Current!.Articles.Single(); Assert.True(updated.Activity!.DistanceMeters > live.Activity.DistanceMeters);
        Assert.Equal(live.Slug, updated.Slug); Assert.Equal(live.PublishedAtUtc, updated.PublishedAtUtc);
        version = await articles.ImportGpxAsync(id, version, null, remove: true);
        Assert.DoesNotContain("data-fitness-route", await client.GetStringAsync($"/admin/posts/{id}/preview"));
        Assert.Contains("data-fitness-route", await client.GetStringAsync($"/posts/{live.Slug}/"));
        Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded); Assert.Null(active.Current.Articles.Single().Activity);
        Assert.True((await publishing.TransitionAsync(id, "unpublish", version)).Succeeded); Assert.Empty(active.Current.Articles);
        Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
        Assert.True((await publishing.TransitionAsync(id, "delete", version)).Succeeded); Assert.Empty(active.Current.Articles);
    }

    [Fact]
    public async Task Hugo_build_and_backup_restore_preserve_tracks_and_keep_pending_imports_private()
    {
        using var data = new TemporaryData(); using var output = new TemporaryData(); using var restored = new TemporaryData(); using var backup = new TemporaryData();
        using var app = new TestApplication(data.Path); using var client = app.Client(); _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope(); var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).FitnessUnits = FitnessUnitSystem.Imperial; await db.SaveChangesAsync(); });
        var id = Guid.NewGuid(); var version = await articles.ImportGpxAsync(id, 0, Upload(), PostType.Fitness);
        Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>(); var published = active.Current!.Articles.Single();
        var replacement = Gpx.Replace("lon=\"0.002\"", "lon=\"0.004\"");
        version = await articles.ImportGpxAsync(id, version, Upload(replacement));
        await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
        {
            using var zip = new ZipArchive(archive); Directory.CreateDirectory(output.Path); zip.ExtractToDirectory(output.Path);
            using var reader = new StreamReader(zip.GetEntry($"content/posts/{published.Slug}/index.md")!.Open());
            var content = await reader.ReadToEndAsync(); Assert.Contains("\"activity\"", content); Assert.DoesNotContain("PRIVATE", content);
            Assert.Contains(published.Activity!.FormatDistance(FitnessUnitSystem.Imperial), content); Assert.Contains("\"fitness_units\": \"imperial\"", content);
            Assert.Contains(zip.Entries, x => x.FullName.Contains("working", StringComparison.Ordinal));
        }
        using (var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo")
        { WorkingDirectory = output.Path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "--panicOnWarning" } })!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }
        var html = File.ReadAllText(Path.Combine(output.Path, "public", "posts", published.Slug, "index.html"));
        Assert.Contains("data-fitness-route", html); Assert.Contains("Heart rate (bpm)", html);
        Assert.Contains("/assets/map/fitness-map.js", html); Assert.True(File.Exists(Path.Combine(output.Path, "public/assets/map/fitness-map.js")));
        Assert.Contains("data-fitness-map-toolbar", html); Assert.DoesNotContain("data-fitness-diagram", html);
        Assert.Contains(published.Activity!.FormatDistance(FitnessUnitSystem.Imperial), html); Assert.Contains("Speed (mi/h)", html); Assert.DoesNotContain("PRIVATE", html);
        await using (var archive = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync())
        {
            Directory.CreateDirectory(backup.Path); var path = Path.Combine(backup.Path, "backup.zip");
            await using (var file = File.Create(path)) await archive.CopyToAsync(file);
            await BackupService.RestoreAsync(path, restored.Path);
        }
        using var fresh = new TestApplication(restored.Path, setupToken: null); using var visitor = fresh.Client();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/health/ready")).StatusCode);
        await using var restoredScope = fresh.Services.CreateAsyncScope();
        var draft = (await restoredScope.ServiceProvider.GetRequiredService<ArticleService>().FindAsync(id))!;
        Assert.Equal(replacement, Encoding.UTF8.GetString(draft.Activity!.OriginalGpx)); Assert.Equal(version, draft.Draft!.Version);
        Assert.Equal(published.Activity.DistanceMeters, fresh.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single().Activity!.DistanceMeters);
        Assert.Equal(FitnessUnitSystem.Imperial, fresh.Services.GetRequiredService<ActiveSite>().Current!.Settings.FitnessUnits);
    }

    private sealed class RenderFault { public bool Fail { get; set; } }
    [Fact]
    public async Task Six_type_theme_packages_use_shared_fitness_fallback()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var theme = app.Services.GetRequiredService<ThemeService>().Resolve("folio");
        theme = theme with { Manifest = theme.Manifest with { PostTypes = theme.Manifest.PostTypes.Where(x => x != "fitness").ToArray() } };
        var post = new ArticleSnapshot(1, Guid.NewGuid(), "activity", "Fitness", "A run", null, DateTime.UtcNow, DateTime.UtcNow, 0, [], null, [])
        { Type = PostType.Fitness, Activity = GpxParser.Parse(Encoding.UTF8.GetBytes(Gpx)) };
        var html = await scope.ServiceProvider.GetRequiredService<IStaticPageRenderer>().RenderAsync(new(new() { CanonicalBaseUrl = "https://example.test" }, post, [], Theme: theme));
        Assert.Contains("data-fitness-route", html); Assert.DoesNotContain("data-folio-post", html);
        var model = ThemePageModel.From(new(new() { CanonicalBaseUrl = "https://example.test" }, post, [], Theme: theme), new SafeMarkdown());
        Assert.Equal(post.Activity, model.Post!.Activity); Assert.Contains(model.Navigation, x => x.Url == "/fitness/");
    }

    private sealed class FaultyRenderer(IServiceProvider services, Microsoft.Extensions.Logging.ILoggerFactory logs, RenderFault fault) : IStaticPageRenderer
    {
        public Task<string> RenderAsync(PublicPage page) => fault.Fail ? throw new IOException("Injected activity rendering failure") : new StaticPageRenderer(services, logs).RenderAsync(page);
    }
}
