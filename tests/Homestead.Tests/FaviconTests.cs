using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Homestead.Tests;

public sealed class FaviconTests
{
    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Every_theme_uses_default_or_custom_favicon_in_listings_posts_and_private_previews(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        await app.WithDatabase(async db =>
        {
            (await db.SiteSettings.SingleAsync()).ThemeId = theme;
            await db.SaveChangesAsync();
        });
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync();
        await articles.SaveAsync(post.Id, new() { Title = "Favicon article", BodyMarkdown = "Public article", Tags = "Home" });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", 1)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>();
        var paths = new[] { "/", "/notes/", "/articles/", "/reviews/", "/guides/", "/galleries/", "/checkins/", "/fitness/", "/about/", "/tags/home/", "/posts/favicon-article/" };
        foreach (var path in paths) AssertIcon(await author.GetStringAsync(path), "/assets/favicon.png");
        AssertIcon(await author.GetStringAsync($"/admin/posts/{post.Id}/preview"), "/logo.png");
        var defaultBytes = await author.GetByteArrayAsync("/logo.png");
        Assert.Equal(defaultBytes, await author.GetByteArrayAsync("/assets/favicon.png"));
        using (var image = SKBitmap.Decode(defaultBytes)) { Assert.NotNull(image); Assert.Equal(256, image.Width); Assert.Equal(256, image.Height); }
        Assert.True(defaultBytes.Length < 40_000);
        Assert.Contains("assets/favicon.png", active.Current!.Files);
        Assert.Contains("href=\"/logo.png?v=", await author.GetStringAsync("/admin/settings"));

        Assert.Equal(HttpStatusCode.Redirect, (await Upload(author)).StatusCode);
        var first = Assert.Single(active.Current!.Branding!);
        foreach (var path in paths) AssertIcon(await author.GetStringAsync(path), first.Url);
        AssertIcon(await author.GetStringAsync($"/admin/posts/{post.Id}/preview"), first.PrivateUrl);
        Assert.Equal(Photo(), await author.GetByteArrayAsync(first.Url));
        Assert.Contains("assets/favicon.png", active.Current.Files); // Homestead attribution keeps the bundled icon.
        Assert.Contains("href=\"/logo.png?v=", await author.GetStringAsync("/admin/settings"));
        var model = ThemePageModel.From(new(active.Current.Settings, null, [], Branding: active.Current.Branding,
            Theme: scope.ServiceProvider.GetRequiredService<ThemeService>().Resolve(theme)), scope.ServiceProvider.GetRequiredService<SafeMarkdown>());
        Assert.Equal(first.Id, model.Site.Favicon!.Id); Assert.Equal(first.Url, model.Site.FaviconUrl); Assert.Equal("image/png", model.Site.FaviconMimeType);

        Assert.Equal(HttpStatusCode.Redirect, (await Upload(author)).StatusCode);
        var second = Assert.Single(active.Current!.Branding!);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(first.Url)).StatusCode);
        AssertIcon(await author.GetStringAsync("/"), second.Url);
        Assert.Equal(HttpStatusCode.Redirect, (await Remove(author)).StatusCode);
        AssertIcon(await author.GetStringAsync("/"), "/assets/favicon.png");
        AssertIcon(await author.GetStringAsync($"/admin/posts/{post.Id}/preview"), "/logo.png");
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(second.Url)).StatusCode);
        await app.WithDatabase(async db =>
        {
            Assert.Null((await db.SiteSettings.SingleAsync()).FaviconMediaId);
            Assert.Equal(2, await db.MediaAssets.CountAsync()); // Replaced originals remain private for backup.
        });
    }

    [Fact]
    public async Task Failed_favicon_changes_keep_active_icon_and_static_export_until_retry_and_restart()
    {
        using var data = new TemporaryData(); var fault = new ActivationFault();
        using (var app = new TestApplication(data.Path, configureServices: services =>
            services.AddSingleton<IGenerationActivation>(provider => new FailingActivation(
                new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), fault))))
        using (var author = app.Client())
        {
            _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(author)).StatusCode);
            var active = app.Services.GetRequiredService<ActiveSite>(); var before = active.Current!;
            var original = Assert.Single(before.Branding!);
            fault.Fail = true;
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(author)).StatusCode);
            Assert.Equal(before.GenerationId, active.Current!.GenerationId);
            AssertIcon(await author.GetStringAsync("/"), original.Url);
            Assert.Contains("previous public site remains active", await author.GetStringAsync("/admin/settings"));
            await app.WithDatabase(async db => Assert.NotEqual(original.Id, (await db.SiteSettings.SingleAsync()).FaviconMediaId));
            await using var scope = app.Services.CreateAsyncScope();
            await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
            using (var zip = new ZipArchive(archive))
            {
                using var html = new StreamReader(zip.GetEntry("index.html")!.Open());
                AssertIcon(await html.ReadToEndAsync(), original.Url);
                Assert.NotNull(zip.GetEntry(original.Url.TrimStart('/')));
            }
            fault.Fail = false;
            Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
            var replacement = Assert.Single(active.Current!.Branding!);
            Assert.NotEqual(original.Id, replacement.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(original.Url)).StatusCode);
            fault.Fail = true;
            Assert.Equal(HttpStatusCode.Redirect, (await Remove(author)).StatusCode);
            AssertIcon(await author.GetStringAsync("/"), replacement.Url);
            Assert.Equal(Photo(), await author.GetByteArrayAsync(replacement.Url));
            fault.Fail = false;
            Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
            AssertIcon(await author.GetStringAsync("/"), "/assets/favicon.png");
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(replacement.Url)).StatusCode);
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var visitor = restarted.Client();
        AssertIcon(await visitor.GetStringAsync("/"), "/assets/favicon.png");
        Assert.NotEmpty(await visitor.GetByteArrayAsync("/assets/favicon.png"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Static_Hugo_and_restored_backup_include_the_selected_favicon(bool custom)
    {
        using var data = new TemporaryData(); using var output = new TemporaryData(); using var restoredData = new TemporaryData();
        using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        if (custom) Assert.Equal(HttpStatusCode.Redirect, (await Upload(author)).StatusCode);
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        var iconPath = custom ? Assert.Single(active.Branding!).Url : "/assets/favicon.png";
        var bytes = await author.GetByteArrayAsync(iconPath);
        await using var scope = app.Services.CreateAsyncScope();
        await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            using var icon = new MemoryStream(); await zip.GetEntry(iconPath.TrimStart('/'))!.Open().CopyToAsync(icon);
            Assert.Equal(bytes, icon.ToArray());
        }
        await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            Directory.CreateDirectory(output.Path); zip.ExtractToDirectory(output.Path);
        }
        using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo")
        {
            WorkingDirectory = output.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "--panicOnWarning", "--baseURL", "https://export.example.test/subsite/" }
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
        AssertIcon(await File.ReadAllTextAsync(Path.Combine(output.Path, "public", "index.html")), "/subsite" + iconPath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(output.Path, "public", iconPath.TrimStart('/'))));
        var backupFile = Path.Combine(output.Path, "backup.zip");
        await using (var archive = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync())
        await using (var destination = File.Create(backupFile)) await archive.CopyToAsync(destination);
        await BackupService.RestoreAsync(backupFile, restoredData.Path);
        using var restored = new TestApplication(restoredData.Path, setupToken: null); using var reader = restored.Client();
        AssertIcon(await reader.GetStringAsync("/"), iconPath);
        Assert.Equal(bytes, await reader.GetByteArrayAsync(iconPath));
    }

    [Fact]
    public void Legacy_settings_default_to_no_custom_favicon()
        => Assert.Null(JsonSerializer.Deserialize<Homestead.Data.Entities.SiteSettings>("{}")!.FaviconMediaId);

    private static void AssertIcon(string html, string url)
    {
        var icon = Regex.Match(html, "<link rel=\"icon\"[^>]*>").Value;
        Assert.Contains($"href=\"{url}\"", icon); Assert.Contains("type=\"image/png\"", icon);
    }
    private static byte[] Photo()
    {
        return TestImages.Create(32, 32);
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient author)
    {
        var html = await author.GetStringAsync("/admin/settings");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(WebUtility.HtmlDecode(token)), "__RequestVerificationToken");
        form.Add(new StringContent("Favicon"), "Slot");
        var image = new ByteArrayContent(Photo()); image.Headers.ContentType = new("image/png");
        form.Add(image, "Upload", "favicon.png");
        return await author.PostAsync("/admin/settings?handler=Upload", form);
    }
    private static Task<HttpResponseMessage> Remove(HttpClient author) => TestApplication.Submit(author, "/admin/settings",
        new() { ["Slot"] = "Favicon" }, "/admin/settings?handler=Remove");
    private sealed class ActivationFault { public bool Fail { get; set; } }
    private sealed class FailingActivation(GenerationActivation inner, ActivationFault fault) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fault.Fail
            ? throw new IOException("Injected favicon activation failure") : inner.ActivateAsync(manifest);
    }
}
