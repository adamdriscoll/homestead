using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class ThemeTests
{
    private static byte[] RazorPackage(Dictionary<string, string> templates, bool mapped = false)
    {
        var manifest = new ThemeManifest(1, "Razor garden", "2.0.0", [
            new(1, ["microblog", "article", "review", "guide", "gallery"], ["theme.css"], []),
            new(2, ["microblog", "article", "review", "guide", "gallery", "checkin"], ["theme.css"], ["theme.js"], true,
                Templates: mapped ? templates.ToDictionary(x => x.Key, x => "templates/" + x.Key + ".razor") : null)]);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string file, string content)
            { using var writer = new StreamWriter(zip.CreateEntry(file).Open()); writer.Write(content); }
            Add("theme.json", JsonSerializer.Serialize(manifest)); Add("theme.css", "/* Razor garden */"); Add("theme.js", "window.garden = true;");
            foreach (var (name, content) in templates) Add((mapped ? "templates/" : "") + name + ".razor", content);
        }
        return buffer.ToArray();
    }

    [Fact]
    public async Task Razor_regions_and_post_precedence_preserve_snapshots_preview_and_private_sources()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author); var postId = await Note(author);
        var package = RazorPackage(new()
        {
            ["Header"] = "<header data-razor-header><a href=\"/\">@Model.Site.Title</a></header>",
            ["Content"] = "<section data-razor-content>@Model.DefaultContent</section>",
            ["Post"] = "<article data-generic-post><a href=\"@Model.Post!.Url\">@Model.Post.DisplayTitle</a>@Model.Post.ContentHtml</article>",
            ["Note"] = "<article data-note-post><a href=\"@Model.Post!.Url\">@Model.Post.DisplayTitle</a>@Model.Post.ContentHtml</article>"
        }, mapped: true);
        var uploadResult = await Upload(author, package);
        Assert.True(uploadResult.StatusCode == HttpStatusCode.Redirect, await uploadResult.Content.ReadAsStringAsync());
        var theme = Assert.Single(app.Services.GetRequiredService<ThemeService>().Installed(), x => x.Id.StartsWith("custom-", StringComparison.Ordinal));
        Assert.Equal(2, theme.EngineVersion);
        var fields = Settings(theme.Id); fields["Input.Title"] = "Garden <script>alert(1)</script>";
        _ = await TestApplication.Submit(author, "/admin/settings", fields);
        foreach (var path in new[] { "/", "/notes/", "/posts/garden-note/" })
        {
            var html = await author.GetStringAsync(path);
            Assert.Contains("data-razor-header", html); Assert.Contains("data-razor-content", html); Assert.Contains("data-note-post", html);
            Assert.DoesNotContain("data-generic-post", html); Assert.DoesNotContain("<script>alert", html); Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("Shared content with {{footer}} text", html); Assert.Contains("Made at home with", html);
            Assert.Contains("https://example.test" + path, html);
        }
        foreach (var path in new[] { "/articles/", "/about/" }) Assert.Contains("data-razor-header", await author.GetStringAsync(path));
        foreach (var path in new[] { "/assets/theme/templates/Note.razor", $"/admin/themes/{theme.Id}/templates/Note.razor" })
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync(path)).StatusCode);
        _ = await TestApplication.Submit(author, $"/admin/posts/{postId}/edit", new()
        { ["Input.Version"] = "1", ["Input.Title"] = "Private note", ["Input.BodyMarkdown"] = "private draft secret" }, $"/admin/posts/{postId}/edit?handler=Save");
        var preview = await author.GetStringAsync($"/admin/posts/{postId}/preview");
        Assert.Contains("private draft secret", preview); Assert.Contains("data-note-post", preview); Assert.DoesNotContain("<script", preview);
        Assert.DoesNotContain("private draft secret", await author.GetStringAsync("/"));
        await using var scope = app.Services.CreateAsyncScope();
        await using var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
        using var zip = new ZipArchive(archive);
        Assert.DoesNotContain(zip.Entries, x => x.FullName.EndsWith(".razor", StringComparison.Ordinal));
        using var home = new StreamReader(zip.GetEntry("index.html")!.Open()); Assert.Contains("data-note-post", await home.ReadToEndAsync());
    }

    [Fact]
    public async Task Razor_main_replaces_document_and_all_six_types_receive_structured_models_and_safe_preview_urls()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        var templates = new Dictionary<string, string>
        {
            ["Main"] = "<html lang=\"en\" data-custom-document>@Model.Head<body>@Model.Header@Model.Content@Model.Footer</body></html>",
            ["Footer"] = "<footer data-custom-footer>@Model.Site.FooterText</footer>"
        };
        foreach (var name in new[] { "Note", "Article", "Review", "Guide", "Gallery", "Checkin" })
            templates[name] = "<article data-type=\"" + name + "\"><h1>@Model.Post!.DisplayTitle</h1>@Model.Post.ContentHtml" +
                "@foreach (var image in Model.Post.Media) { <img src=\"@image.Url\" alt=\"@image.AltText\" /> }" +
                "@foreach (var step in Model.Post.Steps) { <section>@step.BodyHtml</section> }</article>";
        var uploadResult = await Upload(author, RazorPackage(templates));
        Assert.True(uploadResult.StatusCode == HttpStatusCode.Redirect, await uploadResult.Content.ReadAsStringAsync());
        Assert.Contains("installed. Select", await author.GetStringAsync("/admin/settings"));
        var themes = app.Services.GetRequiredService<ThemeService>();
        var theme = themes.Resolve(Assert.Single(themes.Installed(), x => x.Id.StartsWith("custom-", StringComparison.Ordinal)).Id);
        await using var scope = app.Services.CreateAsyncScope(); var renderer = scope.ServiceProvider.GetRequiredService<IStaticPageRenderer>();
        foreach (var type in Enum.GetValues<PostType>())
        {
            var image = new ArticleMedia(Guid.NewGuid(), "photo.png", "image/png", "<private photo>");
            var post = new ArticleSnapshot(1, Guid.NewGuid(), "sample", "<title>", "**Safe** <script>unsafe</script>", null,
                DateTime.UtcNow, DateTime.UtcNow, 0, ["C++", "C#"], image.Id, [image])
            {
                Type = type,
                Review = new("Subject", 4, "https://example.test"),
                Steps = [new(Guid.NewGuid(), "**Step**") { Title = "Mix ingredients", DurationSeconds = 5400 }],
                Tools = [new("Bowl", "https://example.test/bowl")],
                Materials = [new("Flour", null) { Quantity = "10 boxes" }],
                Location = new("Place", "Address", 12, 34),
                TagSlugs = new() { ["C++"] = "c-plus", ["C#"] = "c-sharp" }
            };
            var page = new PublicPage(new() { CanonicalBaseUrl = "https://example.test" }, post, [], Preview: true, Theme: theme);
            var html = await renderer.RenderAsync(page);
            Assert.Contains("data-custom-document", html); Assert.Contains("data-custom-footer", html);
            Assert.Contains("data-type=\"" + (type == PostType.Microblog ? "Note" : type.ToString()) + "\"", html);
            Assert.Contains("&lt;title&gt;", html); Assert.DoesNotContain("<script", html);
            Assert.Contains("src=\"" + image.PrivateUrl + "\"", html); Assert.DoesNotContain("src=\"" + image.Url + "\"", html);
            Assert.Contains("<strong>Step</strong>", html); Assert.Contains("Place", html);
            var model = ThemePageModel.From(page, scope.ServiceProvider.GetRequiredService<SafeMarkdown>());
            Assert.Equal("/tags/c-plus/", model.Post!.Tags[0].Url); Assert.Equal("/tags/c-sharp/", model.Post.Tags[1].Url);
            Assert.Equal(post.Tools, model.Post.Tools); Assert.Equal(post.Materials, model.Post.Materials);
            Assert.Equal(5400, model.Post.Steps.Single().DurationSeconds);
            Assert.Equal("Mix ingredients", model.Post.Steps.Single().Title);
            Assert.Equal("1 hour, 30 minutes", model.Post.Steps.Single().DurationText);
        }
    }

    [Theory]
    [InlineData("<p>@Model.DoesNotExist</p>")]
    [InlineData("@if (true) {")]
    [InlineData("<UnknownComponent />")]
    public async Task Invalid_Razor_is_rejected_before_installation(string source)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var active = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
        _ = await Upload(client, RazorPackage(new() { ["Main"] = source }));
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "themes")));
        Assert.Equal(active, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
        Assert.Contains("Razor", await client.GetStringAsync("/admin/settings"));
    }

    [Fact]
    public async Task Throwing_Razor_keeps_desired_settings_and_previous_active_site_until_retry()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); _ = await Note(client);
        var home = await client.GetStringAsync("/"); var active = app.Services.GetRequiredService<ActiveSite>(); var before = active.Current!.GenerationId;
        _ = await Upload(client, RazorPackage(new() { ["Content"] = "@if (Model.Site.Title == \"Broken\" && Model.Path == \"/about/\") { throw new InvalidOperationException(\"Theme failure\"); }\n@Model.DefaultContent" }));
        var theme = Assert.Single(app.Services.GetRequiredService<ThemeService>().Installed(), x => x.Id.StartsWith("custom-", StringComparison.Ordinal));
        var fields = Settings(theme.Id); fields["Input.Title"] = "Broken";
        _ = await TestApplication.Submit(client, "/admin/settings", fields);
        await app.WithDatabase(async db => Assert.Equal(theme.Id, (await db.SiteSettings.SingleAsync()).ThemeId));
        Assert.Equal(before, active.Current!.GenerationId); Assert.Equal(home, await client.GetStringAsync("/"));
        Assert.Contains("previous public site remains active", await client.GetStringAsync("/admin/settings"));
        fields["Input.Title"] = "Fixed";
        _ = await TestApplication.Submit(client, "/admin/settings", fields);
        Assert.NotEqual(before, active.Current!.GenerationId); Assert.Equal(theme.Id, active.Current.Theme!.Id);
        Assert.Contains("Fixed", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Razor_sources_survive_backup_restore_and_Hugo_export_without_becoming_public_assets()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); string id;
        var archivePath = Path.Combine(data.Path, "backup.zip");
        using (var app = new TestApplication(data.Path))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); _ = await Note(client);
            _ = await Upload(client, RazorPackage(new() { ["Header"] = "<header data-restored-razor>@Model.Site.Title</header>" }));
            id = Assert.Single(app.Services.GetRequiredService<ThemeService>().Installed(), x => x.Id.StartsWith("custom-", StringComparison.Ordinal)).Id;
            _ = await TestApplication.Submit(client, "/admin/settings", Settings(id));
            await using var scope = app.Services.CreateAsyncScope();
            await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
            using (var zip = new ZipArchive(archive))
            {
                Assert.NotNull(zip.GetEntry("homestead-theme/Header.razor"));
                Assert.DoesNotContain(zip.Entries, x => x.FullName.StartsWith("static/", StringComparison.Ordinal) && x.FullName.EndsWith(".razor", StringComparison.Ordinal));
                var root = Path.Combine(data.Path, "hugo-razor"); Directory.CreateDirectory(root); zip.ExtractToDirectory(root);
                using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("HOMESTEAD_HUGO") ?? "hugo")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    ArgumentList = { "--panicOnWarning" }
                })!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await stdout + await stderr);
                Assert.True(File.Exists(Path.Combine(root, "public", "posts", "garden-note", "index.html")));
                Assert.Empty(Directory.GetFiles(Path.Combine(root, "public"), "*.razor", SearchOption.AllDirectories));
            }
            await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
            await using var output = File.Create(archivePath); await backup.CopyToAsync(output);
        }
        await BackupService.RestoreAsync(archivePath, restored.Path);
        using var restarted = new TestApplication(restored.Path, setupToken: null); using var visitor = restarted.Client();
        Assert.Contains("data-restored-razor", await visitor.GetStringAsync("/"));
        Assert.Equal(id, restarted.Services.GetRequiredService<ActiveSite>().Current!.Theme!.Id);
    }

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
        var variant = new ThemeVariant(invalid == "engine" ? 99 : 1,
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
        _ = await TestApplication.Submit(client, created.Headers.Location.ToString(), new()
        { ["NewPostType"] = "Microblog", ["Input.Version"] = "0", ["Input.BodyMarkdown"] = "Shared content with {{footer}} text", ["Input.Title"] = "Garden note" }, $"/admin/posts/{id}/edit?handler=Publish");
        return id;
    }

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Built_in_themes_style_every_public_route_without_changing_administration(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); _ = await Note(client);
        var settings = Settings(theme); settings["Input.ThemePrimaryColor"] = "#123456";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", settings)).StatusCode);
        var routes = new[] { "/", "/notes/", "/articles/", "/reviews/", "/guides/", "/galleries/", "/checkins/", "/about/", "/posts/garden-note/" };
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

    [Theory]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Built_in_Razor_themes_render_all_six_types_with_locations_media_private_preview_and_portable_static_output(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); await PortabilityTests.Fixture(app);
        var settings = Settings(theme); settings["Input.EnableLocationMap"] = "true";
        _ = await TestApplication.Submit(client, "/admin/settings", settings);
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        Assert.Equal(theme, active.Theme!.Id); Assert.Equal(2, active.Theme.EngineVersion);
        var home = await client.GetStringAsync("/");
        var prefix = theme == "folio" ? "folio" : "town";
        Assert.Contains($"data-{prefix}-cover", home); Assert.Contains("Fixture review", home); Assert.Contains("5/5", home);
        Assert.Contains("Theme home", Regex.Match(home, $"(?s)<header data-{prefix}-header>(.*?)</header>").Groups[1].Value);
        Assert.Contains("Fixture place", home); Assert.DoesNotContain("PRIVATE DRAFT", home);
        Assert.DoesNotContain("<script>location", home);
        foreach (var post in active.Articles)
        {
            var html = await client.GetStringAsync($"/posts/{post.Slug}/");
            Assert.Contains($"data-{prefix}-header", html); Assert.Contains("<footer", html);
            Assert.Contains($"data-{prefix}-post", html);
            Assert.Contains("Gallery caption", html); Assert.Contains("Fixture place", html);
            foreach (var image in post.Media) Assert.Contains(image.Url, html);
            if (post.Type == PostType.Guide) Assert.Contains("Second instruction", html);
            if (post.Type == PostType.Review) Assert.Contains("5/5", html);
        }
        foreach (var path in new[] { "/tags/travel/", "/about/", "/map/" })
            Assert.Contains($"data-{prefix}-header", await client.GetStringAsync(path));
        Assert.Contains("data-latitude=\"41.88\"", await client.GetStringAsync("/map/"));
        await using var scope = app.Services.CreateAsyncScope();
        var article = active.Articles.First(x => x.Type == PostType.Article);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var input = ArticleInput.From((await articles.FindAsync(article.PostId))!); input.BodyMarkdown = "PRIVATE FOLIO EDITS";
        await articles.SaveAsync(article.PostId, input);
        var preview = await client.GetStringAsync($"/admin/posts/{article.PostId}/preview");
        Assert.Contains("PRIVATE FOLIO EDITS", preview); Assert.Contains("Private draft preview", preview);
        Assert.Contains(article.Media[0].PrivateUrl, preview); Assert.DoesNotContain("PRIVATE FOLIO EDITS", await client.GetStringAsync($"/posts/{article.Slug}/"));
        foreach (var name in scope.ServiceProvider.GetRequiredService<ThemeService>().Resolve(theme).Variant.Templates!.Keys)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/assets/theme/{name}.razor")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/admin/themes/{theme}/{name}.razor")).StatusCode);
        }
        await using var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
        using var zip = new ZipArchive(archive);
        Assert.DoesNotContain(zip.Entries, x => x.FullName.EndsWith(".razor", StringComparison.Ordinal));
        using var exported = new StreamReader(zip.GetEntry("index.html")!.Open()); Assert.Equal(home, await exported.ReadToEndAsync());
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
            var package = Package();
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(author, package)).StatusCode);
            Assert.Equal(before, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
            var themes = app.Services.GetRequiredService<ThemeService>(); id = themes.Installed().Single(x => x.Id.StartsWith("custom-", StringComparison.Ordinal)).Id;
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(author, package)).StatusCode);
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
        Assert.Equal(ThemeService.BuiltInIds.Count, app.Services.GetRequiredService<ThemeService>().Installed().Count);
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
