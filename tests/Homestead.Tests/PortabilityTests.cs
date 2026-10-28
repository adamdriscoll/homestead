using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class PortabilityTests
{
    [Theory]
    [InlineData("Static")]
    [InlineData("Hugo")]
    [InlineData("Backup")]
    public async Task Downloads_require_authentication_and_antiforgery(string handler)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author);
        Assert.Equal(HttpStatusCode.Redirect, (await author.GetAsync("/admin/maintenance")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await author.PostAsync($"/admin/maintenance?handler={handler}", new FormUrlEncodedContent([]))).StatusCode);
        _ = await TestApplication.Login(author);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsync($"/admin/maintenance?handler={handler}", new FormUrlEncodedContent([]))).StatusCode);
        using var download = await TestApplication.Submit(author, "/admin/maintenance", [], $"/admin/maintenance?handler={handler}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", download.Headers.CacheControl!.ToString());
        using var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync()));
        Assert.NotEmpty(zip.Entries);
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "backups")));
    }

    [Fact]
    public async Task Static_archive_uses_active_manifest_and_survives_shutdown()
    {
        using var data = new TemporaryData(); using var output = new TemporaryData(); Guid draft;
        string[] files; string expected;
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        {
            _ = await TestApplication.Setup(author);
            await Fixture(app);
            var active = app.Services.GetRequiredService<ActiveSite>().Current!;
            files = active.Files; expected = File.ReadAllText(Path.Combine(app.Services.GetRequiredService<ActiveSite>().Root(active), "index.html"));
            await using var scope = app.Services.CreateAsyncScope();
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var post = (await articles.FindAsync(active.Articles.First(x => x.Type == PostType.Article).PostId))!;
            var input = ArticleInput.From(post); input.BodyMarkdown = "CONFIDENTIAL EDITS";
            await articles.SaveAsync(post.Id, input);
            draft = (await articles.CreateAsync()).Id;
            await articles.SaveAsync(draft, new ArticleInput { Title = "Private draft", BodyMarkdown = "CONFIDENTIAL DRAFT" });
            // A missing original causes the desired update to fail while the old site still serves.
            var image = active.Articles.SelectMany(x => x.Media).First();
            var original = scope.ServiceProvider.GetRequiredService<MediaService>().OriginalPath(image);
            var bytes = File.ReadAllBytes(original); File.Delete(original);
            var failed = await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync();
            Assert.False(failed.Succeeded);
            File.WriteAllBytes(original, bytes);
            await using var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
            using var zip = new ZipArchive(archive);
            Directory.CreateDirectory(output.Path); zip.ExtractToDirectory(output.Path);
            Assert.Equal(files.Order(), zip.Entries.Where(x => x.FullName != "README.txt").Select(x => x.FullName).Order());
        }
        // Homestead is now stopped. Every route and local resource exists in the extracted site.
        Assert.Equal(expected, File.ReadAllText(Path.Combine(output.Path, "index.html")));
        foreach (var file in files)
        {
            Assert.True(File.Exists(Path.Combine(output.Path, file)), file);
            if (file.EndsWith(".html") || file.EndsWith(".xml"))
            {
                var text = File.ReadAllText(Path.Combine(output.Path, file));
                Assert.DoesNotContain("CONFIDENTIAL", text);
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "(?:href|src)=\"(/[^\"?#]*)\""))
                {
                    var path = WebUtility.HtmlDecode(match.Groups[1].Value).TrimStart('/');
                    if (path.Length == 0 || path.EndsWith('/')) path += "index.html";
                    Assert.True(File.Exists(Path.Combine(output.Path, path)), path);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hugo_archive_builds_all_types_and_keeps_private_work_out_of_default_build(bool hideCredit)
    {
        using var data = new TemporaryData(); using var output = new TemporaryData();
        using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); await Fixture(app);
        await app.WithDatabase(async db =>
        {
            var settings = await db.SiteSettings.SingleAsync(); settings.Title = "Café 🏡 \"Home\""; settings.HideHomesteadCredit = hideCredit;
            settings.ShareBluesky = settings.ShareFacebook = settings.ShareX = settings.ShareLinkedIn = settings.ShareReddit = settings.ShareEmail = hideCredit;
            await db.SaveChangesAsync();
        });
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var article = (await articles.FindAsync(active.Articles.First(x => x.Type == PostType.Article).PostId))!;
        var edits = ArticleInput.From(article); edits.BodyMarkdown = "PRIVATE PENDING EDITS"; await articles.SaveAsync(article.Id, edits);
        var unpublished = await articles.CreateAsync(); await articles.SaveAsync(unpublished.Id, new() { Title = "Not public", BodyMarkdown = "PRIVATE UNPUBLISHED" });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(unpublished.Id, "publish", 1)).Succeeded);
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(unpublished.Id, "unpublish", 1)).Succeeded);
        var deleted = await articles.CreateAsync(); await articles.SaveAsync(deleted.Id, new() { Title = "Deleted", BodyMarkdown = "DELETED CONTENT" });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(deleted.Id, "delete", 1)).Succeeded);
        await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
        {
            using var zip = new ZipArchive(archive); Directory.CreateDirectory(output.Path); zip.ExtractToDirectory(output.Path);
        }
        var markdown = Directory.GetFiles(Path.Combine(output.Path, "content", "posts"), "index.md", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        Assert.Equal(10, markdown.Length); // seven public, one draft, one working copy, one unpublished
        Assert.Contains(markdown, x => x.Contains("## Step 1: Mix ingredients") && x.Contains("\"title\": \"Mix ingredients\""));
        Assert.Contains(markdown, x => x.Contains("PRIVATE PENDING EDITS") && x.Contains("\"draft\": true"));
        Assert.Contains(markdown, x => x.Contains("PRIVATE UNPUBLISHED") && x.Contains("\"homestead_unpublished\": true"));
        Assert.DoesNotContain(markdown, x => x.Contains("DELETED CONTENT"));
        Assert.Contains(markdown, x => x.Contains("\"rating\": 5"));
        Assert.Contains(markdown, x => x.Contains("Step 2") && x.Contains("Second instruction"));
        Assert.Contains(markdown, x => x.Contains("\"duration_seconds\": 5400") && x.Contains("Time: 1 hour, 30 minutes") && x.Contains("Mixing bowl"));
        Assert.Contains(markdown, x => x.Contains("\"quantity\": \"10 boxes\"") && x.Contains("2 cups flour — 10 boxes"));
        Assert.Contains(markdown, x => x.Contains("Gallery caption 日本語"));
        Assert.Contains(markdown, x => x.Contains("\"type\": \"checkin\"") && x.Contains("\"latitude\": 41.88") && x.Contains("\"name\": \"Fixture place\""));
        var command = Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo";
        using var process = Process.Start(new ProcessStartInfo(command)
        {
            WorkingDirectory = output.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "--panicOnWarning" }
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
        foreach (var post in active.Articles)
        {
            var page = Path.Combine(output.Path, "public", "posts", post.Slug, "index.html");
            Assert.True(File.Exists(page), post.Slug);
            var html = File.ReadAllText(page);
            Assert.DoesNotContain("PRIVATE", html); Assert.DoesNotContain("/admin/", html);
            Assert.Contains("Location: Fixture place", html);
            Assert.Equal(hideCredit, html.Contains("data-social-sharing", StringComparison.Ordinal));
            if (hideCredit)
            {
                foreach (var service in new[] { "Bluesky", "Facebook", "X", "LinkedIn", "Reddit", "Email" }) Assert.Contains($"Share via {service}", html);
                Assert.Contains($"https%3A%2F%2Fexample.test%2Fposts%2F{post.Slug}%2F", html);
                Assert.DoesNotContain("ZgotmplZ", html);
            }
            Assert.DoesNotContain("<script>location", html);
            Assert.DoesNotContain("href=\"https://location.example.test\"", html);
            Assert.Equal(!hideCredit, html.Contains("Made at home with", StringComparison.Ordinal));
            Assert.Equal(!hideCredit, html.Contains("href=\"https://github.com/adamdriscoll/homestead\"", StringComparison.Ordinal));
            if (post.Type == PostType.Guide)
            {
                Assert.Contains("Mixing bowl", html); Assert.Contains("2 cups flour", html);
                Assert.Contains("10 boxes", html);
                Assert.Contains("href=\"https://example.test/bowl\"", html); Assert.Contains("Time: 1 hour, 30 minutes", html);
            }
            foreach (var image in post.Media) Assert.True(File.Exists(Path.Combine(output.Path, "public", "posts", post.Slug, image.Filename)));
        }
        var allPublic = string.Join("\n", Directory.GetFiles(Path.Combine(output.Path, "public"), "*.html", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("PRIVATE", allPublic);
        var privateBundle = Directory.GetFiles(Path.Combine(output.Path, "content", "posts"), "index.md", SearchOption.AllDirectories)
            .Single(path => File.ReadAllText(path).Contains("PRIVATE DRAFT"));
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(privateBundle)!), path => Path.GetExtension(path) is ".png" or ".webp");
        Assert.False(Directory.Exists(Path.Combine(output.Path, "public", "posts", Path.GetFileName(Path.GetDirectoryName(privateBundle)!))));
        Assert.Contains("Gallery caption 日本語", allPublic);
        Assert.Contains("Second instruction", allPublic);
        Assert.Contains("5/5", allPublic);
        var map = File.ReadAllText(Path.Combine(output.Path, "public", "map", "index.html"));
        Assert.Contains("Location map", map);
        Assert.Contains("data-latitude=\"41.88\" data-longitude=\"-87.63\"", map);
        foreach (var post in active.Articles) Assert.Contains($"href=\"/posts/{post.Slug}/\"", map);
        Assert.DoesNotContain("PRIVATE", map);
        foreach (var asset in new[] { "leaflet/leaflet.js", "leaflet/leaflet.css", "leaflet/images/marker-icon.png", "location-map.js" })
            Assert.True(File.Exists(Path.Combine(output.Path, "public", "assets", "map", asset)));
        Assert.Contains("/assets/map/leaflet/leaflet.js", map);
        Assert.True(File.Exists(Path.Combine(output.Path, "public", "tags", "travel", "index.html")));
        var fixtures = Environment.GetEnvironmentVariable("HOMESTEAD_FIXTURE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(fixtures))
        {
            Directory.CreateDirectory(fixtures);
            await using var fixture = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync();
            await using var destination = File.Create(Path.Combine(fixtures, "hugo-fixture.zip")); await fixture.CopyToAsync(destination);
            await using var staticFixture = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
            await using var staticDestination = File.Create(Path.Combine(fixtures, "static-fixture.zip")); await staticFixture.CopyToAsync(staticDestination);
        }
    }

    [Fact]
    public async Task Backup_restores_content_originals_settings_credentials_and_rebuilds_on_fresh_instance()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); using var output = new TemporaryData();
        Directory.CreateDirectory(output.Path); var archivePath = Path.Combine(output.Path, "backup.zip");
        Dictionary<string, string> mediaHashes; Guid[] ids;
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        {
            _ = await TestApplication.Setup(author); await Fixture(app);
            await app.WithDatabase(async db => { var settings = await db.SiteSettings.SingleAsync(); settings.Title = "Restored café 日本語"; settings.AboutMarkdown = "Private ownership"; settings.HideHomesteadCredit = true; await db.SaveChangesAsync(); });
            await using var scope = app.Services.CreateAsyncScope();
            await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
            await using (var file = File.Create(archivePath)) await backup.CopyToAsync(file);
            ids = await scope.ServiceProvider.GetRequiredService<HomesteadDbContext>().Posts.Select(x => x.Id).ToArrayAsync();
            mediaHashes = Directory.GetFiles(Path.Combine(data.Path, "media"), "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(data.Path, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        await BackupService.RestoreAsync(archivePath, restored.Path);
        Assert.False(File.Exists(Path.Combine(restored.Path, "site", "current.json")));
        using var fresh = new TestApplication(restored.Path, setupToken: null); using var client = fresh.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Login(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/setup")).StatusCode);
        await fresh.WithDatabase(async db =>
        {
            Assert.Equal(ids.Order(), (await db.Posts.Select(x => x.Id).ToArrayAsync()).Order());
            Assert.Equal("Restored café 日本語", (await db.SiteSettings.SingleAsync()).Title);
            Assert.True((await db.SiteSettings.SingleAsync()).HideHomesteadCredit);
            Assert.True((await db.SiteSettings.SingleAsync()).EnableLocationMap);
            Assert.Contains("PRIVATE DRAFT", (await db.PostDrafts.ToArrayAsync()).Select(x => x.BodyMarkdown));
            Assert.Equal(new[] { "Mixing bowl", "2 cups flour" }, (await db.GuideSupplies.OrderBy(x => x.Kind).ToArrayAsync()).Select(x => x.Name));
            Assert.Equal("10 boxes", (await db.GuideSupplies.SingleAsync(x => x.Kind == GuideSupplyKind.Material)).Quantity);
            Assert.Equal(5400, (await db.GuideSteps.OrderBy(x => x.Order).FirstAsync()).DurationSeconds);
            Assert.Equal("Mix ingredients", (await db.GuideSteps.OrderBy(x => x.Order).FirstAsync()).Title);
        });
        foreach (var (relative, hash) in mediaHashes)
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(restored.Path, relative)))));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(restored.Path, "keys")));
        Assert.Equal(7, fresh.Services.GetRequiredService<ActiveSite>().Current!.Articles.Length);
        Assert.Equal("Fixture place", fresh.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single(x => x.Type == PostType.Checkin).Location!.Name);
        Assert.DoesNotContain("PRIVATE DRAFT", await client.GetStringAsync("/"));
        Assert.DoesNotContain("Made at home with", await client.GetStringAsync("/"));
        Assert.Contains("Fixture place", await client.GetStringAsync("/map/"));
        Assert.DoesNotContain("PRIVATE DRAFT", await client.GetStringAsync("/map/"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.RestoreAsync(archivePath, restored.Path));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("manifest")]
    public async Task Invalid_backups_fail_without_installing_files(string fault)
    {
        using var data = new TemporaryData(); using var output = new TemporaryData(); using var restored = new TemporaryData();
        Directory.CreateDirectory(output.Path); var path = Path.Combine(output.Path, "bad.zip");
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        {
            _ = await TestApplication.Setup(author);
            await using var scope = app.Services.CreateAsyncScope();
            await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
            await using (var destination = File.Create(path)) await backup.CopyToAsync(destination);
        }
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var manifestEntry = zip.GetEntry("manifest.json")!;
            BackupManifest manifest;
            using (var reader = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<BackupManifest>(reader)!;
            switch (fault)
            {
                case "missing": zip.GetEntry("homestead.db")!.Delete(); break;
                case "duplicate": using (var stream = zip.CreateEntry("homestead.db").Open()) stream.WriteByte(0); break;
                case "version": manifest = manifest with { ApplicationVersion = "unsupported" }; break;
                case "traversal":
                    using (var stream = zip.CreateEntry("../escape.db").Open()) stream.WriteByte(0);
                    manifest = manifest with { Files = [new("../escape.db", 1, "wrong"), .. manifest.Files] }; break;
                case "checksum": manifest = manifest with { Files = manifest.Files.Select(x => x with { Sha256 = "wrong" }).ToArray() }; break;
            }
            manifestEntry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(fault == "manifest" ? "broken JSON" : JsonSerializer.Serialize(manifest));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.RestoreAsync(path, restored.Path));
        Assert.True(!Directory.Exists(restored.Path) || !Directory.EnumerateFileSystemEntries(restored.Path).Any());
    }

    internal static async Task Fixture(TestApplication app)
    {
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).EnableLocationMap = true; await db.SaveChangesAsync(); });
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var uploads = scope.ServiceProvider.GetRequiredService<MediaService>();
        foreach (var type in Enum.GetValues<PostType>())
        {
            var post = await articles.CreateAsync(type);
            await using var image = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png")));
            var asset = await uploads.UploadAsync(post.Id, new FormFile(image, 0, image.Length, "image", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "A café 日本語");
            var step = Guid.NewGuid();
            var input = new ArticleInput
            {
                Title = $"Fixture {type}",
                BodyMarkdown = "A café 日本語 & a quiet day.",
                Tags = "Travel, Café 日本語",
                Subject = "Fixture review",
                Rating = 5,
                ExternalUrl = "https://example.test/coffee",
                LocationName = "Fixture place",
                LocationAddress = "123 Café Street <script>location</script> [link](https://location.example.test)\n# heading",
                Latitude = 41.88,
                Longitude = -87.63,
                Tools = [new() { Name = "Mixing bowl", Url = "https://example.test/bowl" }],
                Materials = [new() { Name = "2 cups flour", Quantity = "10 boxes" }],
                Steps = [new() { Id = step, Title = "Mix ingredients", BodyMarkdown = "First instruction", Duration = TimeSpan.FromMinutes(90) }, new() { Title = "Cook", BodyMarkdown = "Second instruction" }],
                Captions = new() { [asset.Id] = "Gallery caption 日本語" },
                StepImages = type == PostType.Guide ? new() { [asset.Id] = step } : [],
                CoverMediaId = type == PostType.Article ? asset.Id : null
            };
            await articles.SaveAsync(post.Id, input);
            Assert.True((await publishing.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        }
        var draft = await articles.CreateAsync();
        await articles.SaveAsync(draft.Id, new() { Title = "Private draft", BodyMarkdown = "PRIVATE DRAFT" });
        await using var draftImage = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png")));
        _ = await uploads.UploadAsync(draft.Id, new FormFile(draftImage, 0, draftImage.Length, "image", "private-photo.png")
        { Headers = new HeaderDictionary(), ContentType = "image/png" }, "Private photo");
    }
}
