using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Homestead.Features.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class PublicExperienceTests
{
    private static async Task<Guid> Create(TestApplication app, PostType type = PostType.Microblog)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(type)).Id;
    }
    private static Dictionary<string, string> Fields(long version, string body, string title = "", string tags = "") => new()
    { ["Input.Version"] = version.ToString(), ["Input.BodyMarkdown"] = body, ["Input.Title"] = title, ["Input.Tags"] = tags };
    private static async Task Action(HttpClient client, Guid id, string action, Dictionary<string, string> fields)
    {
        var path = $"/admin/posts/{id}/edit";
        var response = await TestApplication.Submit(client, path, fields, path + "?handler=" + action);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    private static byte[] Photo()
    {
        return TestImages.Create();
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient client, string path, string? slot = null)
    {
        var html = await client.GetStringAsync(path);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(WebUtility.HtmlDecode(token)), "__RequestVerificationToken");
        var image = new ByteArrayContent(Photo()); image.Headers.ContentType = new("image/png");
        form.Add(image, "Upload", "garden.png"); form.Add(new StringContent("Garden & sky"), "UploadAlt");
        if (slot is not null) form.Add(new StringContent(slot), "Slot");
        var request = new HttpRequestMessage(HttpMethod.Post, path + "?handler=Upload") { Content = form };
        if (slot is null) request.Headers.Accept.ParseAdd("application/json");
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Notes_publish_text_photos_or_both_with_private_preview_and_absolute_metadata(bool text, bool photo)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var author = app.Client(); using var visitor = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        var id = await Create(app); string? imageUrl = null;
        if (photo)
        {
            var uploaded = await Upload(author, $"/admin/posts/{id}/edit"); Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            using var json = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()); imageUrl = json.RootElement.GetProperty("url").GetString();
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(imageUrl)).StatusCode);
        }
        var body = text ? "A **quiet** afternoon & a [safe link](https://example.test/)" : "";
        await Action(author, id, "Save", Fields(0, body));
        var preview = await author.GetStringAsync($"/admin/posts/{id}/preview");
        Assert.Contains("noindex, nofollow", preview);
        if (photo) Assert.Contains("/admin/media/", preview);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/admin/posts/{id}/preview")).StatusCode);
        await Action(author, id, "Publish", Fields(1, body));
        var post = app.Services.GetRequiredService<ActiveSite>().Current!.Articles.Single();
        Assert.Equal(PostType.Microblog, post.Type); Assert.StartsWith("note-", post.Slug);
        Assert.DoesNotContain("Unpublished edits", await author.GetStringAsync($"/admin/posts/{id}/edit"));
        var html = await visitor.GetStringAsync($"/posts/{post.Slug}/");
        Assert.Contains("Note ·", WebUtility.HtmlDecode(html)); Assert.DoesNotContain("<script", html);
        foreach (var property in new[] { "og:title", "og:description", "og:url", "og:image", "og:type", "twitter:title", "twitter:description", "twitter:card", "twitter:image" })
            Assert.Contains(property, html);
        Assert.Contains($"https://example.test/posts/{post.Slug}/", html);
        Assert.Contains("https://example.test/rss.xml", html);
        if (photo)
        {
            Assert.Contains(imageUrl!, html); Assert.Equal(Photo(), await visitor.GetByteArrayAsync(imageUrl));
            Assert.Contains("https://example.test" + imageUrl, html);
        }
        else Assert.Contains("https://example.test/assets/social-default.png", html);
        var rss = XDocument.Parse(await visitor.GetStringAsync("/rss.xml"));
        var item = rss.Descendants("item").Single(); Assert.Equal(post.DisplayTitle, item.Element("title")!.Value);
        Assert.Equal("https://example.test/posts/" + post.Slug + "/", item.Element("guid")!.Value);
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        if (photo) Assert.Contains("https://example.test" + imageUrl, item.Element(content + "encoded")!.Value);
        Assert.Equal("application/rss+xml", (await visitor.GetAsync("/rss.xml")).Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(await visitor.GetByteArrayAsync("/assets/social-default.png"));
    }

    [Fact]
    public async Task Blank_notes_and_title_only_notes_cannot_publish_and_unknown_types_cannot_be_created()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author); var id = await Create(app);
        await Action(author, id, "Publish", Fields(0, "   ", "Title only"));
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.Contains("needs text or at least one photo", await author.GetStringAsync($"/admin/posts/{id}/edit"));
        Assert.Equal(HttpStatusCode.BadRequest, (await TestApplication.Submit(author, "/admin/posts", new() { ["type"] = "Unknown" }, "/admin/posts?handler=New")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsync($"/admin/posts/{id}/edit?handler=Publish", new FormUrlEncodedContent(Fields(1, "Body")))).StatusCode);
    }

    [Fact]
    public async Task Mixed_feed_tags_and_discovery_track_snapshots_updates_and_removal()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var article = await Create(app, PostType.Article); var note = await Create(app);
        await Action(client, article, "Publish", Fields(0, "Original article & <unsafe>", "First & <story>", "C++, Home"));
        await Action(client, note, "Publish", Fields(0, "Original note", "Later note", "C#, Home"));
        var home = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.True(home.IndexOf("Later note", StringComparison.Ordinal) < home.IndexOf("First & <story>", StringComparison.Ordinal));
        Assert.Contains("Original note", await client.GetStringAsync("/notes/"));
        Assert.DoesNotContain("First &amp;", await client.GetStringAsync("/notes/"));
        Assert.DoesNotContain("Original note", await client.GetStringAsync("/articles/"));
        var active = app.Services.GetRequiredService<ActiveSite>();
        var first = active.Current!.Articles.Single(x => x.PostId == article); var later = active.Current.Articles.Single(x => x.PostId == note);
        Assert.NotEqual(first.TagSlug("C++"), later.TagSlug("C#"));
        Assert.Contains("Later note", await client.GetStringAsync($"/tags/{later.TagSlug("C#")}/"));
        Assert.DoesNotContain("First &amp;", await client.GetStringAsync($"/tags/{later.TagSlug("C#")}/"));
        foreach (var path in new[] { "/reviews/", "/guides/", "/galleries/" }) Assert.Contains("No posts here yet", await client.GetStringAsync(path));
        Assert.Equal(HttpStatusCode.MovedPermanently, (await client.GetAsync("/notes")).StatusCode);
        await Action(client, note, "Save", Fields(1, "PRIVATE DRAFT", "PRIVATE TITLE", "PRIVATE TAG"));
        _ = await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=Rebuild");
        foreach (var file in active.Current!.Files.Where(x => x.EndsWith(".html") || x.EndsWith(".xml")))
            Assert.DoesNotContain("PRIVATE", await client.GetStringAsync("/" + file));
        var items = XDocument.Parse(await client.GetStringAsync("/rss.xml")).Descendants("item").ToArray();
        Assert.Equal(new[] { "Later note", "First & <story>" }, items.Select(x => x.Element("title")!.Value));
        var sitemap = XDocument.Parse(await client.GetStringAsync("/sitemap.xml"));
        Assert.DoesNotContain("admin", sitemap.ToString()); Assert.DoesNotContain("backup", sitemap.ToString());
        Assert.Contains("/about/", sitemap.ToString()); Assert.Contains($"/posts/{later.Slug}/", sitemap.ToString());
        await Action(client, note, "Publish", Fields(2, "Updated public note", "New title", "New tag"));
        Assert.Contains("New title", await client.GetStringAsync("/rss.xml"));
        Assert.DoesNotContain("Later note", await client.GetStringAsync("/rss.xml"));
        Assert.Contains("Updated public note", await client.GetStringAsync($"/posts/{later.Slug}/"));
        await Action(client, note, "Unpublish", Fields(3, ""));
        Assert.DoesNotContain(later.Slug, await client.GetStringAsync("/rss.xml"));
        Assert.DoesNotContain(later.Slug, await client.GetStringAsync("/sitemap.xml"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/posts/{later.Slug}/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/tags/new-tag/")).StatusCode);
    }

    private static Dictionary<string, string> SettingsFields() => new()
    {
        ["Input.Title"] = "Our porch",
        ["Input.CanonicalBaseUrl"] = "https://new.example.test",
        ["Input.DisplayAuthor"] = "Ada",
        ["Input.AccentColor"] = "Forest",
        ["Input.TimeZone"] = "America/Chicago",
        ["Input.Description"] = "Notes & stories",
        ["Input.AboutMarkdown"] = "## Welcome\n\nA **personal** site. <script>evil()</script> [unsafe](javascript:alert(1))",
        ["Input.AuthorBio"] = "From the garden",
        ["Input.ProfileLinks[0].Label"] = "My profile",
        ["Input.ProfileLinks[0].Url"] = "https://profiles.example.test/ada"
    };

    [Fact]
    public async Task Branding_settings_failure_preserves_entire_active_site_and_retry_survives_restart()
    {
        using var data = new TemporaryData(); var failure = new RenderFailure();
        using (var app = new TestApplication(data.Path, configureServices: services => services.AddScoped<IStaticPageRenderer>(sp =>
            new FailingRenderer(sp, sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(), failure))))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
            var id = await Create(app); await Action(client, id, "Publish", Fields(0, "Hello", "Note"));
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(client, "/admin/settings", "Header")).StatusCode);
            var active = app.Services.GetRequiredService<ActiveSite>(); var before = active.Current!.GenerationId;
            var oldHeader = active.Current.Branding!.Single();
            var rss = await client.GetStringAsync("/rss.xml"); var home = await client.GetStringAsync("/");
            failure.Fail = true;
            Assert.Equal(HttpStatusCode.Redirect, (await Upload(client, "/admin/settings", "Header")).StatusCode);
            Assert.Equal(before, active.Current!.GenerationId);
            Assert.Equal(Photo(), await client.GetByteArrayAsync(oldHeader.Url));
            var settingsFields = SettingsFields();
            for (var i = 1; i < 5; i++)
            {
                settingsFields[$"Input.ProfileLinks[{i}].Label"] = ""; settingsFields[$"Input.ProfileLinks[{i}].Url"] = "";
            }
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", settingsFields)).StatusCode);
            Assert.Equal(before, active.Current!.GenerationId); Assert.Equal(rss, await client.GetStringAsync("/rss.xml"));
            Assert.Equal(home, await client.GetStringAsync("/"));
            Assert.Contains("previous public site remains active", await client.GetStringAsync("/admin/settings"));
            failure.Fail = false;
            _ = await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=Rebuild");
            Assert.NotEqual(before, active.Current!.GenerationId);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(oldHeader.Url)).StatusCode);
            foreach (var path in new[] { "/", "/notes/", "/articles/", "/about/", "/posts/note/" })
            {
                var html = await client.GetStringAsync(path);
                Assert.Contains("data-accent=\"forest\"", html); Assert.Contains("Our porch", html);
                Assert.Contains("https://new.example.test" + path, html); Assert.Contains("My profile", html);
            }
            var about = await client.GetStringAsync("/about/");
            Assert.Contains("<strong>personal</strong>", about); Assert.DoesNotContain("<script", about); Assert.DoesNotContain("href=\"javascript:", about);
            Assert.Contains("https://new.example.test", await client.GetStringAsync("/rss.xml"));
            Assert.Contains("https://new.example.test", await client.GetStringAsync("/sitemap.xml"));
            Assert.Contains("Sitemap: https://new.example.test/sitemap.xml", await client.GetStringAsync("/robots.txt"));
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var visitor = restarted.Client();
        Assert.Contains("Our porch", await visitor.GetStringAsync("/"));
        Assert.Contains("https://new.example.test", await visitor.GetStringAsync("/rss.xml"));
    }

    [Theory]
    [InlineData("Avatar")]
    [InlineData("Logo")]
    [InlineData("Header")]
    public async Task Branding_photos_publish_replace_and_remove_without_exposing_old_originals(string slot)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.Redirect, (await Upload(client, "/admin/settings", slot)).StatusCode);
        var first = app.Services.GetRequiredService<ActiveSite>().Current!.Branding!.Single();
        Assert.Equal(Photo(), await client.GetByteArrayAsync(first.Url));
        Assert.Contains(first.Url, await client.GetStringAsync(slot == "Avatar" ? "/about/" : "/"));
        Assert.Equal(HttpStatusCode.Redirect, (await Upload(client, "/admin/settings", slot)).StatusCode);
        var second = app.Services.GetRequiredService<ActiveSite>().Current!.Branding!.Single();
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(first.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", new() { ["Slot"] = slot }, "/admin/settings?handler=Remove")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(second.Url)).StatusCode);
        Assert.Contains("/assets/social-default.png", await client.GetStringAsync("/"));
        await app.WithDatabase(async db => Assert.Equal(2, await db.MediaAssets.CountAsync()));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example.test")]
    [InlineData("https://user:password@example.test")]
    [InlineData("file:///secret")]
    public async Task Unsafe_profile_links_are_rejected_without_changing_settings(string url)
    {
        Assert.False(SettingsInput.IsProfileUrl(url));
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var fields = SettingsFields(); fields["Input.ProfileLinks[0].Url"] = url;
        var response = await TestApplication.Submit(client, "/admin/settings", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Each profile needs", await response.Content.ReadAsStringAsync());
        Assert.Equal("Homestead", app.Services.GetRequiredService<ActiveSite>().Current!.Settings.Title);
    }

    [Fact]
    public void Phase_two_snapshots_deserialize_as_articles()
    {
        var json = "{\"SchemaVersion\":1,\"Title\":\"Original article\",\"Slug\":\"original\",\"Tags\":[\"Home\"],\"Media\":[]}";
        var snapshot = JsonSerializer.Deserialize<ArticleSnapshot>(json)!;
        Assert.Equal(PostType.Article, snapshot.Type); Assert.Equal("home", snapshot.TagSlug("Home"));
    }

    [Fact]
    public async Task Multiple_photos_and_article_cover_use_unique_files_and_complete_social_metadata()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client); var id = await Create(app);
        var photoUrls = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var uploaded = await Upload(client, $"/admin/posts/{id}/edit"); Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            using var metadata = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            photoUrls.Add(metadata.RootElement.GetProperty("url").GetString()!);
        }
        await Action(client, id, "Publish", Fields(0, $"Two photos. Mentioning {photoUrls[0]} as text. [About](/about/)"));
        var active = app.Services.GetRequiredService<ActiveSite>(); var note = active.Current!.Articles.Single();
        Assert.Equal(2, note.Media.Length);
        var noteHtml = await client.GetStringAsync($"/posts/{note.Slug}/");
        foreach (var image in note.Media)
        {
            Assert.Equal(Photo(), await client.GetByteArrayAsync(image.Url)); Assert.Contains($"src=\"{image.Url}\"", noteHtml);
        }
        XNamespace rssContent = "http://purl.org/rss/1.0/modules/content/";
        var noteContent = XDocument.Parse(await client.GetStringAsync("/rss.xml")).Descendants(rssContent + "encoded").Single().Value;
        Assert.Contains("href=\"https://example.test/about/\"", noteContent);
        var articleId = await Create(app, PostType.Article);
        var response = await Upload(client, $"/admin/posts/{articleId}/edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var mediaId = json.RootElement.GetProperty("id").GetGuid(); var mediaUrl = json.RootElement.GetProperty("url").GetString()!;
        var fields = Fields(0, "A longer story", "A <story> & home"); fields["Input.CoverMediaId"] = mediaId.ToString();
        await Action(client, articleId, "Publish", fields);
        var article = active.Current!.Articles.Single(x => x.PostId == articleId);
        var html = await client.GetStringAsync($"/posts/{article.Slug}/");
        foreach (var field in new[] { "og:title", "og:description", "og:url", "og:image", "og:type", "twitter:card", "twitter:title", "twitter:description", "twitter:image" }) Assert.Contains(field, html);
        Assert.Contains("https://example.test" + mediaUrl, html); Assert.Contains("A &lt;story&gt; &amp; home", html);
        Assert.DoesNotContain("<script", html);
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        var rssItem = XDocument.Parse(await client.GetStringAsync("/rss.xml")).Descendants("item")
            .Single(x => x.Element("title")!.Value == article.Title);
        Assert.Contains("https://example.test" + mediaUrl, rssItem.Element(content + "encoded")!.Value);
    }

    [Fact]
    public async Task Legacy_tag_collisions_rebuild_with_saved_tag_slugs_and_correct_admin_status()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var first = await Create(app, PostType.Article); var second = await Create(app, PostType.Article);
        await Action(client, first, "Publish", Fields(0, "First story", "First article", "C++"));
        await Action(client, second, "Publish", Fields(0, "Second story", "Second article", "C#"));
        await app.WithDatabase(async db =>
        {
            foreach (var snapshot in await db.PublishedSnapshots.ToListAsync())
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(snapshot.SnapshotJson)!.AsObject();
                json.Remove("Type"); json.Remove("TagSlugs"); snapshot.SnapshotJson = json.ToJsonString();
            }
            await db.SaveChangesAsync();
        });
        _ = await TestApplication.Submit(client, "/admin/posts", [], "/admin/posts?handler=Rebuild");
        Assert.Contains("First article", await client.GetStringAsync("/tags/c/"));
        Assert.DoesNotContain("Second article", await client.GetStringAsync("/tags/c/"));
        Assert.Contains("Second article", await client.GetStringAsync("/tags/c-2/"));
        Assert.DoesNotContain("Publication pending", await client.GetStringAsync("/admin/posts"));
        Assert.DoesNotContain("still public", await client.GetStringAsync($"/admin/posts/{second}/edit"));
    }

    private sealed class RenderFailure { public bool Fail { get; set; } }
    private sealed class FailingRenderer(IServiceProvider services, Microsoft.Extensions.Logging.ILoggerFactory logs, RenderFailure failure) : IStaticPageRenderer
    {
        public Task<string> RenderAsync(PublicPage page) => failure.Fail && page.Path == "/about/"
            ? throw new IOException("Injected late rendering failure") : new StaticPageRenderer(services, logs).RenderAsync(page);
    }
}
