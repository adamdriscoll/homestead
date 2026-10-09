using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Newsletter;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class NewsletterTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Emails : INewsletterEmailSender
    {
        public bool Fail { get; set; }
        public List<NewsletterEmail> Sent { get; } = [];
        public List<Guid> Attempts { get; } = [];
        public Task SendAsync(string apiKey, string senderEmail, string senderName, NewsletterEmail email, CancellationToken ct)
        {
            Assert.Equal("private-brevo-key", apiKey); Assert.Equal("owner@example.test", senderEmail);
            Assert.Equal("Owner", senderName); Attempts.Add(email.Id);
            if (Fail) throw new IOException("Provider detail containing private-brevo-key");
            Sent.Add(email); return Task.CompletedTask;
        }
    }
    private static TestApplication Application(TemporaryData data, Emails emails, Clock clock) => new(data.Path, configureServices: services =>
    {
        services.Remove(services.Single(x => x.ImplementationType == typeof(NewsletterWorker)));
        services.AddSingleton<INewsletterEmailSender>(emails); services.AddSingleton<TimeProvider>(clock);
    });
    private static NewsletterInput Configuration(bool enabled = true) => new()
    { Enabled = enabled, ApiKey = "private-brevo-key", SenderEmail = "owner@example.test", SenderName = "Owner" };
    private static async Task Enable(TestApplication app)
    {
        var clock = (Clock)app.Services.GetRequiredService<TimeProvider>();
        if (clock.Now < DateTimeOffset.UtcNow) clock.Now = DateTimeOffset.UtcNow;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NewsletterService>().SaveAsync(Configuration());
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
    }
    private static async Task Run(TestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NewsletterService>().RunAsync();
    }
    private static string Token(NewsletterEmail email) => QueryHelpers.ParseQuery(new Uri(Regex.Match(email.Text, "https://[^\\s]+").Value).Query)["token"].ToString();
    private static async Task<string> Subscribe(TestApplication app, Emails emails, string email = "reader@example.test",
        NewsletterFrequency frequency = NewsletterFrequency.Instant, params PostType[] types)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NewsletterService>();
        var clock = (Clock)app.Services.GetRequiredService<TimeProvider>();
        if (clock.Now < DateTimeOffset.UtcNow) clock.Now = DateTimeOffset.UtcNow;
        await service.RequestSubscriptionAsync(new() { Email = email, Frequency = frequency, PostTypes = types.Length == 0 ? Enum.GetValues<PostType>().ToList() : types.ToList() });
        var token = Token(emails.Sent.Last());
        var manage = await service.ConfirmAsync(token);
        Assert.NotNull(manage); return manage;
    }
    private static async Task<Guid> Publish(TestApplication app, string title)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>(); var post = await articles.CreateAsync();
        var version = await articles.SaveAsync(post.Id, new() { Title = title, BodyMarkdown = "Published text <script>unsafe</script>" });
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", version)).Succeeded);
        return post.Id;
    }

    [Fact]
    public async Task Confirmation_requires_POST_preserves_existing_preferences_and_unsubscribe_works_when_disabled()
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        using var app = Application(data, emails, clock); using var client = app.Client();
        _ = await TestApplication.Setup(client); await Enable(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/newsletter/", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        var fields = new Dictionary<string, string> { ["Input.Email"] = "Reader@Example.test", ["Input.Frequency"] = "Weekly", ["Input.PostTypes"] = "Article" };
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/newsletter/", fields)).StatusCode);
        var confirmation = Token(Assert.Single(emails.Sent)); var path = "/newsletter/?token=" + Uri.EscapeDataString(confirmation);
        var response = await client.GetAsync(path);
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        await app.WithDatabase(async db => Assert.False((await db.NewsletterSubscribers.SingleAsync()).Subscribed));
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, path, new() { ["Token"] = confirmation }, "/newsletter/?handler=Confirm")).StatusCode);
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<NewsletterService>();
        var db = scope.ServiceProvider.GetRequiredService<Homestead.Data.HomesteadDbContext>();
        var subscriber = await db.NewsletterSubscribers.AsNoTracking().SingleAsync();
        Assert.True(subscriber.Subscribed); Assert.Equal(NewsletterFrequency.Weekly, subscriber.Frequency); Assert.Equal(1 << (int)PostType.Article, subscriber.PostTypes);
        Assert.Null(await service.ConfirmAsync(confirmation)); // Single-use confirmation.
        clock.Now += TimeSpan.FromMinutes(16);
        await service.RequestSubscriptionAsync(new() { Email = subscriber.Email, Frequency = NewsletterFrequency.Instant });
        await app.WithDatabase(async database => Assert.Equal(NewsletterFrequency.Weekly, (await database.NewsletterSubscribers.SingleAsync()).Frequency));
        var management = service.ManagementToken(subscriber);
        await service.SaveAsync(Configuration(false));
        var managePath = "/newsletter/?manage=" + Uri.EscapeDataString(management);
        Assert.Contains("Unsubscribe", await client.GetStringAsync(managePath));
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, managePath, new() { ["Manage"] = management }, "/newsletter/?handler=Unsubscribe")).StatusCode);
        await app.WithDatabase(async database => Assert.False((await database.NewsletterSubscribers.SingleAsync()).Subscribed));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/newsletter/?manage=tampered")).StatusCode);
    }

    [Fact]
    public async Task Instant_notices_use_live_snapshots_filter_types_retry_durably_and_do_not_repeat_edits()
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        Guid id;
        using (var app = Application(data, emails, clock))
        {
            using var client = app.Client(); _ = await TestApplication.Setup(client);
            await Publish(app, "Old post"); await Enable(app);
            await Subscribe(app, emails, types: [PostType.Article]); emails.Sent.Clear();
            id = await Publish(app, "New & public");
            await using var scope = app.Services.CreateAsyncScope(); var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var input = ArticleInput.From((await articles.FindAsync(id))!); input.Title = "PRIVATE draft"; await articles.SaveAsync(id, input);
            emails.Fail = true; await Run(app);
            Assert.Empty(emails.Sent);
            await app.WithDatabase(async db =>
            {
                Assert.DoesNotContain("private-brevo-key", (await db.NewsletterSettings.SingleAsync()).LastError!);
                Assert.Equal(NewsletterDeliveryStatus.Pending, (await db.NewsletterDeliveries.SingleAsync()).Status);
            });
            var attempts = emails.Attempts.Count;
            await Run(app); Assert.Equal(attempts, emails.Attempts.Count);
        }
        emails.Fail = false; clock.Now += TimeSpan.FromMinutes(2);
        using var restarted = Application(data, emails, clock); using var visitor = restarted.Client();
        await Task.WhenAll(Run(restarted), Run(restarted));
        var sent = Assert.Single(emails.Sent);
        Assert.Contains("New & public", sent.Text); Assert.DoesNotContain("PRIVATE", sent.Text); Assert.DoesNotContain("Old post", sent.Text);
        Assert.Contains("https://example.test/posts/new-public/", sent.Text); Assert.Contains("Preferences or unsubscribe", sent.Text);
        Assert.DoesNotContain("<script>", sent.Html); Assert.Equal(emails.Attempts[^2], emails.Attempts[^1]);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var articleService = restartedScope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = (await articleService.FindAsync(id))!;
        Assert.True((await restartedScope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(id, "publish", post.Draft!.Version)).Succeeded);
        await Run(restarted); Assert.Single(emails.Sent);
    }

    [Fact]
    public async Task Weekly_summaries_include_selected_types_and_skip_removed_posts_and_empty_windows()
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        using var app = Application(data, emails, clock); using var client = app.Client(); _ = await TestApplication.Setup(client);
        await Enable(app); await Subscribe(app, emails, frequency: NewsletterFrequency.Weekly, types: [PostType.Article, PostType.Guide]);
        await Subscribe(app, emails, email: "notes@example.test", frequency: NewsletterFrequency.Instant, types: [PostType.Microblog]);
        emails.Sent.Clear(); await PortabilityTests.Fixture(app); // Publishes all six types.
        await Run(app); var note = Assert.Single(emails.Sent); Assert.Equal("notes@example.test", note.To);
        emails.Sent.Clear();
        var removed = await Publish(app, "Removed before digest");
        await using var scope = app.Services.CreateAsyncScope(); var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = (await articles.FindAsync(removed))!;
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(removed, "unpublish", post.Draft!.Version)).Succeeded);
        clock.Now += TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1); await Run(app); Assert.Empty(emails.Sent);
        clock.Now += TimeSpan.FromSeconds(1); await Run(app);
        var digest = Assert.Single(emails.Sent); Assert.Contains("weekly summary", digest.Subject);
        var current = app.Services.GetRequiredService<ActiveSite>().Current!;
        foreach (var item in current.Articles)
            if (item.Type is PostType.Article or PostType.Guide) Assert.Contains("/posts/" + item.Slug + "/", digest.Text);
            else Assert.DoesNotContain("/posts/" + item.Slug + "/", digest.Text);
        Assert.DoesNotContain("Removed before digest", digest.Text);
        clock.Now += TimeSpan.FromDays(7); await Run(app); Assert.Single(emails.Sent);
    }

    private sealed class ActivationFailure : IGenerationActivation
    {
        public bool Fail { get; set; }
        public IGenerationActivation Inner { get; set; } = null!;
        public Task ActivateAsync(GenerationManifest manifest) => Fail ? throw new IOException("Injected activation failure") : Inner.ActivateAsync(manifest);
    }

    [Fact]
    public async Task Instant_notices_cover_all_six_types_and_confirmation_expires_without_changing_preferences()
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        using var app = Application(data, emails, clock); using var client = app.Client(); _ = await TestApplication.Setup(client);
        await Enable(app); var manage = await Subscribe(app, emails); emails.Sent.Clear();
        await PortabilityTests.Fixture(app); await Run(app);
        Assert.Equal(6, emails.Sent.Count);
        foreach (var post in app.Services.GetRequiredService<ActiveSite>().Current!.Articles)
            Assert.Single(emails.Sent, email => email.Text.Contains($"/posts/{post.Slug}/", StringComparison.Ordinal));
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<NewsletterService>();
        Assert.True(await service.ManageAsync(manage, new() { Email = "reader@example.test", Frequency = NewsletterFrequency.Weekly, PostTypes = [PostType.Review] }));
        clock.Now += TimeSpan.FromMinutes(16);
        await service.RequestSubscriptionAsync(new() { Email = "reader@example.test", Frequency = NewsletterFrequency.Instant });
        var token = Token(emails.Sent.Last());
        clock.Now += TimeSpan.FromHours(24);
        Assert.Null(await service.ConfirmAsync(token));
        await app.WithDatabase(async db =>
        {
            var subscriber = await db.NewsletterSubscribers.SingleAsync();
            Assert.Equal(NewsletterFrequency.Weekly, subscriber.Frequency); Assert.Equal(1 << (int)PostType.Review, subscriber.PostTypes);
        });
        Assert.True(await service.ManageAsync(manage, null));
        await Run(app); Assert.Equal(7, emails.Sent.Count);
    }

    [Fact]
    public async Task Failed_activation_sends_nothing_and_later_rebuild_announces_new_post_once()
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock(); var failure = new ActivationFailure();
        using var app = new TestApplication(data.Path, configureServices: services =>
        {
            services.Remove(services.Single(x => x.ImplementationType == typeof(NewsletterWorker)));
            services.AddSingleton<INewsletterEmailSender>(emails); services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IGenerationActivation>(provider =>
            { failure.Inner = new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()); return failure; });
        });
        using var client = app.Client(); _ = await TestApplication.Setup(client); await Enable(app); await Subscribe(app, emails); emails.Sent.Clear();
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>(); var post = await articles.CreateAsync();
        var version = await articles.SaveAsync(post.Id, new() { Title = "Pending post", BodyMarkdown = "Committed text" });
        var publisher = scope.ServiceProvider.GetRequiredService<PublishingService>(); failure.Fail = true;
        Assert.False((await publisher.TransitionAsync(post.Id, "publish", version)).Succeeded);
        await Run(app); Assert.Empty(emails.Sent);
        await app.WithDatabase(async db => Assert.DoesNotContain(await db.NewsletterPosts.ToListAsync(), x => x.PostId == post.Id));
        failure.Fail = false; Assert.True((await publisher.RebuildAsync()).Succeeded);
        await Run(app); Assert.Single(emails.Sent); await Run(app); Assert.Single(emails.Sent);
    }

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Every_theme_supports_subscriptions_listings_posts_and_private_previews_without_exporting_reader_data(string theme)
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        using var app = Application(data, emails, clock); using var client = app.Client(); _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).ThemeId = theme; await db.SaveChangesAsync(); });
        await PortabilityTests.Fixture(app); await Enable(app);
        await Subscribe(app, emails); await Run(app); Assert.Single(emails.Sent); // No existing-post backlog.
        var form = await client.GetStringAsync("/newsletter/");
        Assert.Contains($"data-public-theme=\"{theme}\"", form); Assert.Contains("name=\"__RequestVerificationToken\"", form); Assert.Contains("Weekly summary", form);
        var manifest = app.Services.GetRequiredService<ActiveSite>().Current!;
        foreach (var item in manifest.Articles)
        {
            foreach (var path in new[] { "/", $"/posts/{item.Slug}/", $"/admin/posts/{item.PostId}/preview" })
            {
                var html = await client.GetStringAsync(path);
                Assert.Contains("https://example.test/newsletter/", html); Assert.DoesNotContain("private-brevo-key", html);
                Assert.DoesNotContain("reader@example.test", html); Assert.DoesNotContain("Confirm subscription", html);
            }
        }
        Assert.DoesNotContain("reader@example.test", JsonSerializer.Serialize(manifest));
        await using var scope = app.Services.CreateAsyncScope();
        foreach (var stream in new[] { await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync(),
            await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync() })
        {
            await using (stream)
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
                foreach (var entry in zip.Entries.Where(x => x.FullName.EndsWith(".html", StringComparison.Ordinal) || x.FullName.EndsWith(".json", StringComparison.Ordinal)))
                {
                    using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
                    Assert.DoesNotContain("private-brevo-key", text); Assert.DoesNotContain("reader@example.test", text);
                }
        }
    }

    [Fact]
    public async Task Settings_require_authentication_and_antiforgery_retain_protected_key_and_validate_enabling()
    {
        using var data = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        using var app = Application(data, emails, clock); using var client = app.Client(); _ = await TestApplication.Setup(client);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin/settings")).StatusCode);
        _ = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/settings?handler=Newsletter", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        var fields = new Dictionary<string, string> { ["Newsletter.Enabled"] = "true", ["Newsletter.SenderEmail"] = "owner@example.test", ["Newsletter.SenderName"] = "Owner" };
        var invalid = await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Newsletter");
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode); Assert.Contains("Enter a Brevo API key", await invalid.Content.ReadAsStringAsync());
        fields["Newsletter.ApiKey"] = "private-brevo-key";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Newsletter")).StatusCode);
        fields.Remove("Newsletter.ApiKey");
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Newsletter")).StatusCode);
        Assert.DoesNotContain("private-brevo-key", await client.GetStringAsync("/admin/settings"));
        await app.WithDatabase(async db => Assert.NotEqual("private-brevo-key", (await db.NewsletterSettings.SingleAsync()).ProtectedApiKey));
        fields["Newsletter.Enabled"] = "false"; fields["Newsletter.ClearApiKey"] = "true";
        _ = await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Newsletter");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/newsletter/")).StatusCode);
        await app.WithDatabase(async db => Assert.Empty((await db.NewsletterSettings.SingleAsync()).ProtectedApiKey));
    }

    [Fact]
    public async Task Backup_restores_subscribers_keys_management_links_and_sent_history()
    {
        using var data = new TemporaryData(); using var restoredData = new TemporaryData(); var emails = new Emails(); var clock = new Clock();
        var archive = Path.Combine(Path.GetTempPath(), "newsletter-backup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            string manage;
            using (var app = Application(data, emails, clock))
            {
                using var client = app.Client(); _ = await TestApplication.Setup(client); await Enable(app);
                manage = await Subscribe(app, emails); await Publish(app, "Backed up notice"); await Run(app);
                await using var scope = app.Services.CreateAsyncScope();
                await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
                await using var output = File.Create(archive); await backup.CopyToAsync(output);
            }
            await BackupService.RestoreAsync(archive, restoredData.Path);
            using var restored = Application(restoredData, emails, clock); using var visitor = restored.Client();
            await using var restoredScope = restored.Services.CreateAsyncScope();
            var service = restoredScope.ServiceProvider.GetRequiredService<NewsletterService>();
            Assert.NotNull(await service.ResolveAsync(manage, false));
            var count = emails.Sent.Count; await Run(restored); Assert.Equal(count, emails.Sent.Count);
            await restored.WithDatabase(async db =>
            {
                Assert.True((await db.NewsletterSettings.SingleAsync()).Enabled); Assert.Single(await db.NewsletterSubscribers.ToListAsync());
                Assert.Equal(NewsletterDeliveryStatus.Sent, (await db.NewsletterDeliveries.SingleAsync()).Status);
            });
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }
}
