using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class ThemeTests
{
    private static Dictionary<string, string> Settings(string theme = "porch") => new()
    {
        ["Input.Title"] = "Theme home",
        ["Input.DisplayAuthor"] = "Ada",
        ["Input.CanonicalBaseUrl"] = "https://example.test",
        ["Input.TimeZone"] = "UTC",
        ["Input.AccentColor"] = "Ocean",
        ["Input.ThemeId"] = theme
    };

    private static byte[] Package(string? invalid = null)
    {
        var variant = new ThemeVariant(invalid == "engine" ? 2 : 1,
            invalid == "types" ? ["article"] : ["microblog", "article", "review", "guide", "gallery"],
            [invalid == "missing" ? "missing.css" : "v1/theme.css"], ["v1/theme.js"], true, "v1/layout.html");
        var manifest = new ThemeManifest(1, "Garden <theme>", "1.2.0", [variant,
            new(3, ["future-type"], ["v3/theme.css"], [])]);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string file, string content, int attributes = 0)
            {
                var entry = zip.CreateEntry(file); entry.ExternalAttributes = attributes;
                using var writer = new StreamWriter(entry.Open()); writer.Write(content);
            }
            Add("theme.json", invalid == "json" ? "not JSON" : JsonSerializer.Serialize(manifest));
            Add("v1/", "");
            Add("v1/theme.css", "html[data-public-theme] { --color-base-100: #f8f8f8; }", invalid == "symlink" ? 0xa000 << 16 : 0);
            Add("v1/theme.js", "window.themeLoaded = true;");
            Add("v1/layout.html", invalid == "layout" ? "<script>{{header}}{{main}}{{footer}}</script>" :
                invalid == "cdata" ? "{{header}}<![CDATA[{{main}}]]>{{footer}}" :
                "<div class=\"garden-layout\">{{header}}<section aria-label=\"Posts\">{{main}}</section>{{footer}}</div>");
            Add("v3/theme.css", "/* Future engine resources */");
            if (invalid == "traversal") Add("../escape.css", "bad");
            if (invalid == "duplicate") Add("V1/theme.css", "duplicate");
            if (invalid == "newline") Add("v1\n/escape.css", "bad");
            if (invalid == "device") Add("v1/con.css", "bad");
            if (invalid == "razor") Add("page.cshtml", "@{ System.IO.File.Delete(\"anything\"); }");
            if (invalid == "large") Add("large.css", new string('a', ThemeService.MaximumExpandedBytes));
        }
        return buffer.ToArray();
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] package, bool token = true)
    {
        using var form = new MultipartFormDataContent();
        if (token)
        {
            var html = await client.GetStringAsync("/admin/settings");
            var value = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            form.Add(new StringContent(WebUtility.HtmlDecode(value)), "__RequestVerificationToken");
        }
        form.Add(new ByteArrayContent(package), "Upload", "garden.zip");
        return await client.PostAsync("/admin/settings?handler=Theme", form);
    }

    private static async Task<Guid> Note(HttpClient client)
    {
        var created = await TestApplication.Submit(client, "/admin/posts", new() { ["type"] = "Microblog" }, "/admin/posts?handler=New");
        var id = Guid.Parse(created.Headers.Location!.ToString().Split('/')[3]);
        _ = await TestApplication.Submit(client, $"/admin/posts/{id}/edit", new()
        { ["Input.Version"] = "0", ["Input.BodyMarkdown"] = "Shared content with {{footer}} text", ["Input.Title"] = "Garden note" }, $"/admin/posts/{id}/edit?handler=Publish");
        return id;
    }

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    public async Task Built_in_themes_style_every_public_route_without_changing_administration(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); _ = await Note(client);
        var settings = Settings(theme); settings["Input.ThemePrimaryColor"] = "#123456";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", settings)).StatusCode);
        var routes = new[] { "/", "/notes/", "/articles/", "/reviews/", "/guides/", "/galleries/", "/about/", "/posts/garden-note/" };
        foreach (var path in routes)
        {
            var html = await client.GetStringAsync(path);
            Assert.Contains($"data-public-theme=\"{theme}\"", html);
            Assert.Contains("/assets/theme/theme.css", html); Assert.Contains("id=\"main\"", html);
            Assert.Contains("https://example.test" + path, html);
            Assert.Contains("Made at home with", html);
            Assert.Contains("href=\"https://github.com/PoshTools/homestead\"", html);
        }
        Assert.Contains("--color-primary:#123456", await client.GetStringAsync("/assets/theme-colors.css"));
        var admin = await client.GetStringAsync("/admin");
        Assert.DoesNotContain("data-public-theme", admin); Assert.DoesNotContain("/assets/theme/", admin);
        Assert.Contains("admin-appearance", admin);
        settings["Input.HideHomesteadCredit"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", settings)).StatusCode);
        foreach (var path in routes) Assert.DoesNotContain("Made at home with", await client.GetStringAsync(path));
    }

    [Fact]
    public async Task Custom_theme_layout_assets_colors_preview_and_packages_survive_restart()
    {
        using var data = new TemporaryData(); string id; Guid postId;
        using (var app = new TestApplication(data.Path))
        using (var author = app.Client())
        using (var visitor = app.Client())
        {
            _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author); postId = await Note(author);
            var before = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(author, Package())).StatusCode);
            Assert.Equal(before, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
            var themes = app.Services.GetRequiredService<ThemeService>(); id = themes.Installed().Single(x => x.Id.StartsWith("custom-", StringComparison.Ordinal)).Id;
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(author, Package())).StatusCode);
            Assert.Single(themes.Installed(), x => x.Id.StartsWith("custom-", StringComparison.Ordinal));
            var fields = Settings(id); fields["Input.ThemeBackgroundColor"] = "#f8f8f8"; fields["Input.ThemeTextColor"] = "#123456";
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, "/admin/settings", fields)).StatusCode);
            var html = await visitor.GetStringAsync("/");
            Assert.Contains("class=\"garden-layout\"", html); Assert.Contains("/assets/theme/v1/theme.js", html);
            Assert.Contains("Shared content with {{footer}} text", html);
            Assert.Contains("Made at home with", html);
            Assert.Contains("window.themeLoaded", await visitor.GetStringAsync("/assets/theme/v1/theme.js"));
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/assets/theme/theme.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/assets/theme/v1/layout.html")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/themes/" + id + ".zip")).StatusCode);
            var preview = await author.GetAsync($"/admin/posts/{postId}/preview");
            var previewHtml = await preview.Content.ReadAsStringAsync();
            Assert.Contains("garden-layout", previewHtml); Assert.DoesNotContain("<script", previewHtml);
            Assert.Contains("Made at home with", previewHtml);
            Assert.Contains($"/admin/themes/{id}/v1/theme.css", previewHtml);
            Assert.Contains("script-src 'none'", preview.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("no-store", preview.Headers.CacheControl!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await author.GetAsync($"/admin/themes/{id}/v1/theme.css")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/admin/themes/{id}/v1/theme.css")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"/admin/themes/{id}/v1/theme.js")).StatusCode);
            Assert.Equal(id, app.Services.GetRequiredService<ActiveSite>().Current!.Theme!.Id);
            fields["Input.HideHomesteadCredit"] = "true";
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, "/admin/settings", fields)).StatusCode);
            Assert.DoesNotContain("Made at home with", await visitor.GetStringAsync("/"));
            Assert.DoesNotContain("Made at home with", await author.GetStringAsync($"/admin/posts/{postId}/preview"));
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var client = restarted.Client();
        Assert.Contains("garden-layout", await client.GetStringAsync("/"));
        Assert.Equal(id, restarted.Services.GetRequiredService<ActiveSite>().Current!.Theme!.Id);
        Assert.DoesNotContain("Made at home with", await client.GetStringAsync("/"));
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("types")]
    [InlineData("missing")]
    [InlineData("json")]
    [InlineData("layout")]
    [InlineData("cdata")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("newline")]
    [InlineData("device")]
    [InlineData("razor")]
    [InlineData("symlink")]
    [InlineData("large")]
    public async Task Invalid_theme_archives_are_rejected_without_installing_or_changing_publication(string invalid)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var generation = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
        Assert.Equal(HttpStatusCode.Redirect, (await Upload(client, Package(invalid))).StatusCode);
        Assert.DoesNotContain("installed. Select", await client.GetStringAsync("/admin/settings"));
        Assert.Equal(3, app.Services.GetRequiredService<ThemeService>().Installed().Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "themes")));
        Assert.Equal(generation, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
    }

    [Fact]
    public async Task Theme_upload_requires_authentication_and_antiforgery_and_settings_reject_unknown_themes_or_CSS()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        Assert.Equal(HttpStatusCode.Redirect, (await Upload(client, Package(), false)).StatusCode);
        _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(client, Package(), false)).StatusCode);
        foreach (var (key, value) in new[] { ("Input.ThemeId", "../evil"), ("Input.ThemeId", ""), ("Input.ThemePrimaryColor", "red"), ("Input.ThemeTextColor", "#abcd") })
        {
            var fields = Settings(); fields[key] = value;
            Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
        }
        await app.WithDatabase(async db => Assert.Equal("Homestead", (await db.SiteSettings.SingleAsync()).Title));
    }

    private sealed class Failure { public bool Enabled { get; set; } }
    private sealed class FailingRenderer(IServiceProvider services, Microsoft.Extensions.Logging.ILoggerFactory logs, Failure failure) : IStaticPageRenderer
    {
        public Task<string> RenderAsync(PublicPage page) => failure.Enabled && page.Path == "/about/"
            ? throw new IOException("Injected theme build failure") : new StaticPageRenderer(services, logs).RenderAsync(page);
    }

    [Fact]
    public async Task Failed_theme_change_preserves_active_files_and_static_export_then_retries_all_desired_settings()
    {
        using var data = new TemporaryData(); var failure = new Failure();
        using var app = new TestApplication(data.Path, configureServices: services => services.AddScoped<IStaticPageRenderer>(sp =>
            new FailingRenderer(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), failure)));
        using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var active = app.Services.GetRequiredService<ActiveSite>(); var before = active.Current!.GenerationId;
        var home = await client.GetStringAsync("/"); var css = await client.GetStringAsync("/assets/theme/theme.css");
        Assert.Contains("Made at home with", home);
        failure.Enabled = true;
        var fields = Settings("ledger"); fields["Input.HideHomesteadCredit"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields)).StatusCode);
        await app.WithDatabase(async db => Assert.True((await db.SiteSettings.SingleAsync()).HideHomesteadCredit));
        Assert.False(active.Current!.Settings.HideHomesteadCredit);
        Assert.Equal(before, active.Current!.GenerationId); Assert.Equal(home, await client.GetStringAsync("/"));
        Assert.Equal(css, await client.GetStringAsync("/assets/theme/theme.css"));
        Assert.Contains("Currently public: Porch", await client.GetStringAsync("/admin/settings"));
        await using var scope = app.Services.CreateAsyncScope();
        await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
        {
            using var html = new StreamReader(zip.GetEntry("index.html")!.Open()); Assert.Equal(home, await html.ReadToEndAsync());
            using var style = new StreamReader(zip.GetEntry("assets/theme/theme.css")!.Open()); Assert.Equal(css, await style.ReadToEndAsync());
        }
        failure.Enabled = false;
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
        Assert.NotEqual(before, active.Current!.GenerationId); Assert.Equal("ledger", active.Current.Theme!.Id);
        Assert.True(active.Current.Settings.HideHomesteadCredit);
        Assert.DoesNotContain("Made at home with", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Backups_restore_custom_theme_and_Hugo_carries_layout_assets_and_colors()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); string id;
        var archivePath = Path.Combine(data.Path, "backup.zip");
        using (var app = new TestApplication(data.Path))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); _ = await Note(client);
            _ = await Upload(client, Package());
            id = app.Services.GetRequiredService<ThemeService>().Installed().Single(x => x.Id.StartsWith("custom-", StringComparison.Ordinal)).Id;
            var fields = Settings(id); fields["Input.ThemePrimaryColor"] = "#123456";
            _ = await TestApplication.Submit(client, "/admin/settings", fields);
            await using var scope = app.Services.CreateAsyncScope();
            await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
            using (var zip = new ZipArchive(archive))
            {
                Assert.NotNull(zip.GetEntry("static/assets/theme/v1/theme.js")); Assert.NotNull(zip.GetEntry("homestead-theme/theme.json"));
                using var layout = new StreamReader(zip.GetEntry("layouts/baseof.html")!.Open());
                var html = await layout.ReadToEndAsync(); Assert.Contains("garden-layout", html); Assert.Contains("assets/theme/v1/theme.css", html);
                using var colors = new StreamReader(zip.GetEntry("static/assets/theme-colors.css")!.Open()); Assert.Contains("#123456", await colors.ReadToEndAsync());
                var hugoRoot = Path.Combine(data.Path, "hugo-theme");
                Directory.CreateDirectory(hugoRoot); zip.ExtractToDirectory(hugoRoot);
                using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo")
                {
                    WorkingDirectory = hugoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "--panicOnWarning" }
                })!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
                Assert.Contains("garden-layout", await File.ReadAllTextAsync(Path.Combine(hugoRoot, "public", "posts", "garden-note", "index.html")));
                Assert.True(File.Exists(Path.Combine(hugoRoot, "public", "assets", "theme", "v1", "theme.js")));
            }
            await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
            await using var output = File.Create(archivePath); await backup.CopyToAsync(output);
        }
        await BackupService.RestoreAsync(archivePath, restored.Path);
        Assert.True(File.Exists(Path.Combine(restored.Path, "themes", id + ".zip")));
        using var newApp = new TestApplication(restored.Path, setupToken: null); using var visitor = newApp.Client();
        Assert.Contains("garden-layout", await visitor.GetStringAsync("/"));
        Assert.Contains("#123456", await visitor.GetStringAsync("/assets/theme-colors.css"));
    }
}
