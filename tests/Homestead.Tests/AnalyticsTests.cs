using System.Data.Common;
using System.Net;
using Homestead.Data;
using Homestead.Features.Analytics;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class AnalyticsTests
{
    private const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    [Theory]
    [InlineData(Browser, true)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", false)]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", false)]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)", false)]
    [InlineData("Mozilla/5.0 (compatible; ChatGPT-User/1.0; +https://openai.com/bot)", false)]
    [InlineData("ClaudeBot/1.0", false)]
    [InlineData("Claude-User/1.0", false)]
    [InlineData("PerplexityBot/1.0", false)]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 HeadlessChrome/130.0.0.0 Safari/537.36", false)]
    [InlineData("curl/8.10.1", false)]
    [InlineData("Wget/1.21.4", false)]
    [InlineData("python-requests/2.32.3", false)]
    [InlineData("", false)]
    public void User_agents_exclude_obvious_automation(string agent, bool expected)
        => Assert.Equal(expected, AnalyticsService.IsHuman(agent));

    [Fact]
    public async Task Only_successful_anonymous_public_pages_count_with_canonical_paths_and_atomic_increments()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var owner = app.Client(); using var visitor = app.Client();
        _ = await TestApplication.Setup(owner); _ = await TestApplication.Login(owner);
        visitor.DefaultRequestHeaders.UserAgent.ParseAdd(Browser);
        owner.DefaultRequestHeaders.UserAgent.ParseAdd(Browser);
        _ = await visitor.GetStringAsync("/");
        await app.WithDatabase(async db => Assert.Empty(await db.PageViews.ToListAsync()));
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/admin/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.PostAsync("/admin/analytics", new FormUrlEncodedContent(new Dictionary<string, string> { ["Enabled"] = "true" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync("/admin/analytics", new FormUrlEncodedContent(new Dictionary<string, string> { ["Enabled"] = "true" }))).StatusCode);
        Assert.False(app.Services.GetRequiredService<AnalyticsService>().Enabled);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(owner, "/admin/analytics", new() { ["Enabled"] = "true" })).StatusCode);
        _ = await visitor.GetStringAsync("/?private-query=never-store-this");
        _ = await visitor.GetStringAsync("/index.html");
        _ = await visitor.GetStringAsync("/about/");
        _ = await visitor.GetStringAsync("/about/index.html");
        Assert.Equal(HttpStatusCode.MovedPermanently, (await visitor.GetAsync("/about")).StatusCode);
        foreach (var path in new[] { "/rss.xml", "/sitemap.xml", "/assets/site.css", "/missing-page/", "/admin", "/admin/analytics", "/account/login", "/setup", "/health/live", "/newsletter/" })
            _ = await visitor.GetAsync(path);
        _ = await visitor.SendAsync(new(HttpMethod.Head, "/"));
        _ = await visitor.PostAsync("/", new StringContent(""));
        foreach (var path in new[] { "/", "/about/", "/admin", "/admin/analytics" }) _ = await owner.GetAsync(path);
        foreach (var agent in new[] { "", "GPTBot/1.2", "ClaudeBot/1.0", "curl/8.10.1" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.TryAddWithoutValidation("User-Agent", agent);
            _ = await visitor.SendAsync(request);
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => visitor.GetStringAsync("/")));
        await app.WithDatabase(async db =>
        {
            var pages = await db.PageViews.ToDictionaryAsync(x => x.Path, x => x.Hits);
            Assert.Equal(2, pages.Count); Assert.Equal(22, pages["/"]); Assert.Equal(2, pages["/about/"]);
        });
        var dashboard = await owner.GetStringAsync("/admin");
        Assert.Contains("Total page views", dashboard); Assert.Contains(">24<", dashboard);
        var analytics = await owner.GetStringAsync("/admin/analytics");
        Assert.Contains("/about/", analytics); Assert.DoesNotContain("private-query", analytics);
        _ = await TestApplication.Submit(owner, "/admin/analytics", new() { ["Enabled"] = "false" });
        _ = await visitor.GetStringAsync("/");
        await app.WithDatabase(async db => Assert.Equal(24, await db.PageViews.SumAsync(x => x.Hits)));
    }

    [Fact]
    public async Task Enabled_state_and_counts_survive_restart_and_full_backup_restore()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); using var output = new TemporaryData();
        Directory.CreateDirectory(output.Path); var archive = Path.Combine(output.Path, "backup.zip");
        using (var app = new TestApplication(data.Path))
        using (var owner = app.Client())
        using (var visitor = app.Client())
        {
            _ = await TestApplication.Setup(owner);
            await app.Services.GetRequiredService<AnalyticsService>().SetEnabledAsync(true);
            visitor.DefaultRequestHeaders.UserAgent.ParseAdd(Browser);
            _ = await visitor.GetStringAsync("/");
            await using var scope = app.Services.CreateAsyncScope();
            await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
            await using var file = File.Create(archive); await backup.CopyToAsync(file);
        }
        await BackupService.RestoreAsync(archive, restored.Path);
        foreach (var path in new[] { data.Path, restored.Path })
        {
            using var app = new TestApplication(path, setupToken: null); using var visitor = app.Client();
            visitor.DefaultRequestHeaders.UserAgent.ParseAdd(Browser);
            _ = await visitor.GetStringAsync("/");
            Assert.True(app.Services.GetRequiredService<AnalyticsService>().Enabled);
            await app.WithDatabase(async db => Assert.Equal(2, (await db.PageViews.SingleAsync()).Hits));
        }
    }

    [Fact]
    public async Task All_built_in_themes_count_listings_and_posts_but_never_private_previews_or_change_exports()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path);
        using var owner = app.Client(); using var visitor = app.Client();
        _ = await TestApplication.Setup(owner); _ = await TestApplication.Login(owner);
        visitor.DefaultRequestHeaders.UserAgent.ParseAdd(Browser);
        owner.DefaultRequestHeaders.UserAgent.ParseAdd(Browser);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync();
        _ = await articles.SaveAsync(post.Id, new ArticleInput { Title = "Counted post", BodyMarkdown = "Public content" });
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        Assert.True((await publishing.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        await app.Services.GetRequiredService<AnalyticsService>().SetEnabledAsync(true);
        foreach (var theme in new[] { "porch", "journal", "ledger", "folio", "townsquare" })
        {
            await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).ThemeId = theme; await db.SaveChangesAsync(); });
            Assert.True((await publishing.RebuildAsync()).Succeeded);
            foreach (var path in new[] { "/", "/articles/", "/posts/counted-post/" })
                Assert.Contains("Public content", await visitor.GetStringAsync(path));
            Assert.Contains("Public content", await owner.GetStringAsync($"/admin/posts/{post.Id}/preview"));
        }
        await app.WithDatabase(async db => Assert.Equal(15, await db.PageViews.SumAsync(x => x.Hits)));
        await using var zip = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
        using var archive = new System.IO.Compression.ZipArchive(zip);
        Assert.DoesNotContain(archive.Entries, x => x.FullName.Contains("analytics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Statistics_write_failure_does_not_break_static_serving()
    {
        var fault = new AnalyticsFault();
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path, configureServices: services =>
            services.AddDbContext<HomesteadDbContext>((_, options) => options.AddInterceptors(fault)));
        using var client = app.Client(); _ = await TestApplication.Setup(client);
        await app.Services.GetRequiredService<AnalyticsService>().SetEnabledAsync(true);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Browser); fault.Enabled = true;
        Assert.Contains("No posts here yet", await client.GetStringAsync("/"));
        await app.WithDatabase(async db => Assert.Empty(await db.PageViews.ToListAsync()));
    }

    private sealed class AnalyticsFault : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("INSERT INTO \"PageViews\"", StringComparison.Ordinal))
                throw new SqliteException("Injected analytics failure", 10);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
