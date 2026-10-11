using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class SocialSharingTests
{
    private static readonly string[] Services = ["Bluesky", "Facebook", "X", "LinkedIn", "Reddit", "Email"];

    private static Dictionary<string, string> Fields(string theme = "porch", params string[] services)
    {
        var fields = new Dictionary<string, string>
        {
            ["Input.Title"] = "Homestead",
            ["Input.DisplayAuthor"] = "Ada",
            ["Input.TimeZone"] = "UTC",
            ["Input.CanonicalBaseUrl"] = "https://sharing.example.test",
            ["Input.ThemeId"] = theme,
            ["Section"] = "social"
        };
        foreach (var service in services) fields[$"Input.Share{service}"] = "true";
        return fields;
    }

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task All_types_and_listings_share_only_published_titles_and_canonical_URLs_in_every_theme(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await PortabilityTests.Fixture(app);
        Assert.DoesNotContain("data-social-sharing", await client.GetStringAsync("/"));
        var settings = await client.GetStringAsync("/admin/settings");
        Assert.Contains("href=\"#social\"", settings);
        foreach (var service in Services)
            Assert.DoesNotContain("checked=", Regex.Match(settings, $"<input[^>]*id=\"Input_Share{service}\"[^>]*>").Value);

        var response = await TestApplication.Submit(client, "/admin/settings", Fields(theme, Services));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/settings#social", response.Headers.Location!.ToString());
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var active = app.Services.GetRequiredService<ActiveSite>();
        foreach (var post in active.Current!.Articles)
        {
            var draft = ArticleInput.From((await articles.FindAsync(post.PostId))!);
            draft.Title = "PRIVATE title & ? 日本語";
            await articles.SaveAsync(post.PostId, draft);
        }
        Assert.True((await publisher.RebuildAsync()).Succeeded);
        foreach (var post in active.Current!.Articles)
        {
            foreach (var path in new[] { $"/posts/{post.Slug}/", "/", TypePath(post.Type), $"/tags/{post.TagSlug(post.Tags[0])}/" })
            {
                var html = await client.GetStringAsync(path);
                Assert.DoesNotContain("PRIVATE", html);
                foreach (var link in SocialSharing.Links(active.Current.Settings, post.Slug, post.DisplayTitle))
                    Assert.Contains($"href=\"{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(link.Url)}\"", html);
                Assert.Contains("aria-label=\"Share post\"", html);
                Assert.DoesNotContain("https%3A%2F%2Flocalhost", html);
            }
            var preview = await client.GetStringAsync($"/admin/posts/{post.PostId}/preview");
            Assert.Contains("PRIVATE", preview); Assert.DoesNotContain("data-social-sharing", preview);
        }
        Assert.DoesNotContain("data-social-sharing", await client.GetStringAsync("/about/"));
        settings = await client.GetStringAsync("/admin/settings");
        foreach (var service in Services)
            Assert.Contains("checked=", Regex.Match(settings, $"<input[^>]*id=\"Input_Share{service}\"[^>]*>").Value);
    }

    [Fact]
    public async Task Services_can_be_selected_independently_and_all_can_be_disabled()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync(PostType.Microblog);
        await articles.SaveAsync(post.Id, new() { BodyMarkdown = "A public note" });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", 1)).Succeeded);
        foreach (var selected in Services.Select(service => new[] { service }).Append(["Bluesky", "Email"]).Append([]))
        {
            Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", Fields("porch", selected))).StatusCode);
            var html = await client.GetStringAsync("/");
            foreach (var service in Services)
                Assert.Equal(selected.Contains(service), html.Contains($"aria-label=\"Share via {service}\"", StringComparison.Ordinal));
            Assert.Equal(selected.Length > 0, html.Contains("data-social-sharing", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Sharing_encodes_each_parameter_and_removes_email_header_breaks()
    {
        var settings = new SiteSettings
        {
            CanonicalBaseUrl = "https://canonical.example.test/",
            ShareBluesky = true,
            ShareFacebook = true,
            ShareX = true,
            ShareLinkedIn = true,
            ShareReddit = true,
            ShareEmail = true
        };
        const string title = "Café 日本語 & <script> ? # \"hello\"\r\nBcc:private@example.test";
        const string cleanTitle = "Café 日本語 & <script> ? # \"hello\"Bcc:private@example.test";
        const string canonical = "https://canonical.example.test/posts/cafe/";
        var links = SocialSharing.Links(settings, "cafe", title).ToDictionary(x => x.Label, x => x.Url);
        Assert.Equal(6, links.Count);
        var prefixes = new Dictionary<string, string>
        {
            ["Bluesky"] = "https://bsky.app/intent/compose?",
            ["Facebook"] = "https://www.facebook.com/sharer/sharer.php?",
            ["X"] = "https://twitter.com/intent/tweet?",
            ["LinkedIn"] = "https://www.linkedin.com/sharing/share-offsite/?",
            ["Reddit"] = "https://www.reddit.com/submit?",
            ["Email"] = "mailto:?"
        };
        foreach (var (service, link) in links)
        {
            Assert.StartsWith(prefixes[service], link);
            var query = QueryHelpers.ParseQuery(link[link.IndexOf('?')..]);
            Assert.Equal(canonical, query[service == "Facebook" ? "u" : service == "Email" ? "body" : service == "Bluesky" ? "text" : "url"].ToString()
                .Replace(cleanTitle + " ", "", StringComparison.Ordinal));
            if (service is "X" or "Reddit" or "Email")
                Assert.Equal(cleanTitle, query[service == "X" ? "text" : service == "Reddit" ? "title" : "subject"].ToString());
            Assert.DoesNotContain("<script>", link); Assert.DoesNotContain("%0D", link); Assert.DoesNotContain("%0A", link);
        }
        Assert.Empty(SocialSharing.Links(settings, "private-draft", title, preview: true));
        Assert.Empty(SocialSharing.Links(JsonSerializer.Deserialize<SiteSettings>("{}")!, "legacy", title));
    }

    [Fact]
    public async Task Failed_settings_activation_keeps_active_sharing_and_static_export_until_retry_and_restart()
    {
        using var data = new TemporaryData(); var fault = new ActivationFault();
        using (var app = new TestApplication(data.Path, configureServices: services => services.AddSingleton<IGenerationActivation>(provider =>
            new FailingActivation(new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), fault))))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
            await PortabilityTests.Fixture(app);
            _ = await TestApplication.Submit(client, "/admin/settings", Fields("porch", "Email"));
            var active = app.Services.GetRequiredService<ActiveSite>(); var generation = active.Current!.GenerationId;
            fault.Fail = true;
            _ = await TestApplication.Submit(client, "/admin/settings", Fields("porch", "Bluesky"));
            Assert.Contains("Settings saved.", await client.GetStringAsync("/admin/settings"));
            await app.WithDatabase(async db => { var saved = await db.SiteSettings.SingleAsync(); Assert.True(saved.ShareBluesky); Assert.False(saved.ShareEmail); });
            Assert.Equal(generation, active.Current!.GenerationId); Assert.True(active.Current.Settings.ShareEmail);
            Assert.Contains("Share via Email", await client.GetStringAsync("/"));
            Assert.DoesNotContain("Share via Bluesky", await client.GetStringAsync("/"));
            await using var scope = app.Services.CreateAsyncScope();
            await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
            {
                using var zip = new ZipArchive(archive); using var reader = new StreamReader(zip.GetEntry("index.html")!.Open());
                var html = await reader.ReadToEndAsync(); Assert.Contains("Share via Email", html); Assert.DoesNotContain("Share via Bluesky", html);
            }
            await using (var archive = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
            {
                using var zip = new ZipArchive(archive); using var reader = new StreamReader(zip.GetEntry("layouts/partials/social-sharing.html")!.Open());
                var partial = await reader.ReadToEndAsync(); Assert.Contains("Share via Bluesky", partial); Assert.DoesNotContain("Share via Email", partial);
            }
            fault.Fail = false;
            Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
            Assert.Contains("Share via Bluesky", await client.GetStringAsync("/")); Assert.DoesNotContain("Share via Email", await client.GetStringAsync("/"));
        }
        using var restarted = new TestApplication(data.Path, setupToken: null); using var visitor = restarted.Client(false);
        Assert.Contains("Share via Bluesky", await visitor.GetStringAsync("/"));
        await restarted.WithDatabase(async db => Assert.True((await db.SiteSettings.SingleAsync()).ShareBluesky));
    }

    private static string TypePath(PostType type) => type switch
    {
        PostType.Microblog => "/notes/",
        PostType.Article => "/articles/",
        PostType.Review => "/reviews/",
        PostType.Guide => "/guides/",
        PostType.Gallery => "/galleries/",
        PostType.Checkin => "/checkins/",
        PostType.Fitness => "/fitness/",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    private sealed class ActivationFault { public bool Fail { get; set; } }
    private sealed class FailingActivation(GenerationActivation inner, ActivationFault fault) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fault.Fail ? throw new IOException("Injected activation failure") : inner.ActivateAsync(manifest);
    }
}
