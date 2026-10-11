using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class ScheduledPublicationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fault { public bool Fail { get; set; } }
    private sealed class Activation(IGenerationActivation inner, Fault fault) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fault.Fail ? throw new IOException("Injected activation failure") : inner.ActivateAsync(manifest);
    }

    private static TestApplication Application(string path, Clock clock, Fault? fault = null) => new(path, configureServices: services =>
    {
        services.Remove(services.Single(x => x.ImplementationType == typeof(ScheduledPublicationWorker)));
        services.AddSingleton<TimeProvider>(clock);
        if (fault is not null) services.AddSingleton<IGenerationActivation>(provider => new Activation(
            new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), fault));
    });

    private static async Task Run(TestApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PublishingService>().RunSchedulesAsync();
    }

    private static async Task<Guid> Draft(TestApplication app, PostType type = PostType.Article, string title = "Scheduled story")
    {
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync(type);
        if (type == PostType.Gallery)
        {
            using var bytes = new MemoryStream(TestImages.Create());
            await scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(post.Id,
                new FormFile(bytes, 0, bytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "Scheduled photo");
        }
        await articles.SaveAsync(post.Id, new()
        {
            Title = title,
            BodyMarkdown = "FROZEN scheduled content",
            Subject = "Scheduled review",
            Rating = 4,
            LocationName = "Scheduled place",
            Tags = "scheduled",
            Steps = type == PostType.Guide ? [new() { BodyMarkdown = "Scheduled instructions" }] : []
        });
        return post.Id;
    }

    private static async Task<PublicationResult> Schedule(TestApplication app, Guid id, DateTime due, long version = 1)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PublishingService>().ScheduleAsync(id, version, due);
    }

    [Theory]
    [InlineData(PostType.Microblog)]
    [InlineData(PostType.Article)]
    [InlineData(PostType.Review)]
    [InlineData(PostType.Guide)]
    [InlineData(PostType.Gallery)]
    [InlineData(PostType.Checkin)]
    [InlineData(PostType.Fitness)]
    public async Task Frozen_content_stays_private_until_due_and_publishes_once_for_every_type(PostType type)
    {
        using var data = new TemporaryData(); var clock = new Clock();
        using var app = Application(data.Path, clock); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        var id = await Draft(app, type); var due = clock.Now.UtcDateTime.AddHours(1);
        Assert.True((await Schedule(app, id, due)).Succeeded);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = (await articles.FindAsync(id))!;
        var frozen = JsonSerializer.Deserialize<ArticleSnapshot>(post.Schedule!.SnapshotJson)!;
        var input = ArticleInput.From(post); input.BodyMarkdown = "PRIVATE later draft";
        await articles.SaveAsync(id, input);
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
        await Run(app);
        Assert.DoesNotContain("FROZEN", await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/posts/{frozen.Slug}/")).StatusCode);
        foreach (var image in frozen.Media) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(image.Url)).StatusCode);
        clock.Now = new DateTimeOffset(due);
        await Task.WhenAll(Run(app), Run(app));
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        var live = Assert.Single(active.Articles);
        Assert.Equal(due, live.PublishedAtUtc); Assert.Equal("FROZEN scheduled content", live.BodyMarkdown);
        Assert.DoesNotContain("PRIVATE later draft", await client.GetStringAsync($"/posts/{live.Slug}/"));
        Assert.Contains("FROZEN", await client.GetStringAsync("/rss.xml"));
        foreach (var image in frozen.Media) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(image.Url)).StatusCode);
        await Run(app);
        Assert.Equal(active.GenerationId, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
        await app.WithDatabase(async db =>
        {
            Assert.Empty(await db.PostSchedules.ToArrayAsync());
            Assert.Equal("PRIVATE later draft", (await db.PostDrafts.SingleAsync(x => x.PostId == id)).BodyMarkdown);
        });
    }

    [Fact]
    public void Timezone_conversion_requires_explicit_DST_choice_and_rejects_nonexistent_times()
    {
        var spring = new DateTime(2030, 3, 10, 2, 30, 0);
        Assert.Throws<InvalidOperationException>(() => PublishingService.ResolveScheduleTime(spring, "America/Chicago"));
        var fall = new DateTime(2030, 11, 3, 1, 30, 0);
        Assert.Throws<InvalidOperationException>(() => PublishingService.ResolveScheduleTime(fall, "America/Chicago"));
        Assert.Equal(new DateTime(2030, 11, 3, 6, 30, 0, DateTimeKind.Utc), PublishingService.ResolveScheduleTime(fall, "America/Chicago", "-05:00"));
        Assert.Equal(new DateTime(2030, 11, 3, 7, 30, 0, DateTimeKind.Utc), PublishingService.ResolveScheduleTime(fall, "America/Chicago", "-06:00"));
        foreach (var offset in new[] { "+04:00", "garbage", "05:00", "-99:00" })
            Assert.Throws<InvalidOperationException>(() => PublishingService.ResolveScheduleTime(fall, "America/Chicago", offset));
    }

    [Fact]
    public async Task Replace_cancel_and_manual_actions_preserve_public_state_and_reject_stale_tabs()
    {
        using var data = new TemporaryData(); var clock = new Clock();
        using var app = Application(data.Path, clock); using var client = app.Client(); _ = await TestApplication.Setup(client);
        var id = await Draft(app); var due = clock.Now.UtcDateTime.AddHours(1);
        Assert.True((await Schedule(app, id, due)).Succeeded);
        await Assert.ThrowsAsync<DraftConflictException>(() => Schedule(app, id, due.AddHours(1)));
        Assert.True((await Schedule(app, id, due.AddHours(1), 2)).Succeeded);
        await using var scope = app.Services.CreateAsyncScope();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        await Assert.ThrowsAsync<DraftConflictException>(() => publishing.CancelScheduleAsync(id, 2));
        Assert.True((await publishing.CancelScheduleAsync(id, 3)).Succeeded);
        clock.Now = new DateTimeOffset(due.AddHours(2)); await Run(app);
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.True((await publishing.TransitionAsync(id, "publish", 4)).Succeeded);
        var live = Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        var version = 4L;
        foreach (var action in new[] { "publish", "unpublish", "delete" })
        {
            Assert.True((await Schedule(app, id, clock.Now.UtcDateTime.AddHours(1), version)).Succeeded);
            version++;
            Assert.True((await publishing.TransitionAsync(id, action, version)).Succeeded);
            await app.WithDatabase(async db => Assert.Empty(await db.PostSchedules.ToArrayAsync()));
            if (action == "unpublish") Assert.True((await publishing.TransitionAsync(id, "publish", version)).Succeeded);
            if (action != "delete")
            {
                var current = Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
                Assert.Equal(live.Slug, current.Slug); Assert.Equal(live.PublishedAtUtc, current.PublishedAtUtc);
            }
        }
    }

    [Fact]
    public async Task Failed_activation_retries_after_restart_without_replaying_or_exposing_drafts()
    {
        using var data = new TemporaryData(); var clock = new Clock(); var fault = new Fault(); Guid id; DateTime due;
        using (var app = Application(data.Path, clock, fault))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); id = await Draft(app); due = clock.Now.UtcDateTime.AddHours(1);
            Assert.True((await Schedule(app, id, due)).Succeeded);
            var generation = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
            fault.Fail = true; clock.Now = new DateTimeOffset(due); await Run(app);
            Assert.Equal(generation, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
            await app.WithDatabase(async db =>
            {
                var schedule = await db.PostSchedules.SingleAsync(); Assert.True(schedule.Committed); Assert.NotNull(schedule.Error);
                Assert.Equal(due.AddMinutes(5), schedule.NextAttemptAtUtc);
            });
            await using var scope = app.Services.CreateAsyncScope(); var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
            Assert.False((await publishing.CancelScheduleAsync(id, 2)).Succeeded);
            Assert.False((await publishing.ScheduleAsync(id, 2, due.AddDays(1))).Succeeded);
            await Run(app); Assert.Equal(generation, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
        }
        fault.Fail = false; clock.Now = clock.Now.AddMinutes(5);
        using var restarted = Application(data.Path, clock, fault); using var visitor = restarted.Client();
        _ = await visitor.GetAsync("/health/ready"); await Run(restarted);
        var live = Assert.Single(restarted.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.Equal(id, live.PostId); Assert.Equal(due, live.PublishedAtUtc);
        await restarted.WithDatabase(async db => Assert.Empty(await db.PostSchedules.ToArrayAsync()));
    }

    [Fact]
    public async Task Missed_schedules_survive_backup_and_timezone_changes_and_hugo_keeps_frozen_bundle_private()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); using var archives = new TemporaryData();
        var clock = new Clock(); Guid id; var due = clock.Now.UtcDateTime.AddHours(1);
        Directory.CreateDirectory(archives.Path); var backupPath = Path.Combine(archives.Path, "backup.zip");
        using (var app = Application(data.Path, clock))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client); id = await Draft(app);
            Assert.True((await Schedule(app, id, due)).Succeeded);
            await using var scope = app.Services.CreateAsyncScope();
            var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
            var input = ArticleInput.From((await articles.FindAsync(id))!); input.BodyMarkdown = "PRIVATE later edits";
            await articles.SaveAsync(id, input);
            await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).TimeZone = "America/Chicago"; await db.SaveChangesAsync(); });
            await using (var hugo = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync())
            using (var zip = new ZipArchive(hugo, ZipArchiveMode.Read))
            {
                var entry = Assert.Single(zip.Entries, x => x.FullName.Contains("-scheduled-", StringComparison.Ordinal) && x.FullName.EndsWith("index.md", StringComparison.Ordinal));
                using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
                Assert.Contains("\"draft\": true", text); Assert.Contains("homestead_scheduled_at", text);
                Assert.Contains("FROZEN scheduled content", text); Assert.DoesNotContain("PRIVATE later edits", text);
            }
            await using var backup = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
            await using var file = File.Create(backupPath); await backup.CopyToAsync(file);
        }
        await BackupService.RestoreAsync(backupPath, restored.Path); clock.Now = new DateTimeOffset(due.AddDays(1));
        using var fresh = Application(restored.Path, clock); using var visitor = fresh.Client(); _ = await visitor.GetAsync("/health/ready");
        Assert.Empty(fresh.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        await Run(fresh);
        var live = Assert.Single(fresh.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.Equal(id, live.PostId); Assert.Equal(due, live.PublishedAtUtc); Assert.Equal("FROZEN scheduled content", live.BodyMarkdown);
        await fresh.WithDatabase(async db => Assert.Equal("PRIVATE later edits", (await db.PostDrafts.SingleAsync()).BodyMarkdown));
    }

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task All_themes_preview_drafts_and_render_scheduled_updates_with_stable_urls(string theme)
    {
        using var data = new TemporaryData(); var clock = new Clock();
        using var app = Application(data.Path, clock); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).ThemeId = theme; await db.SaveChangesAsync(); });
        var id = await Draft(app); await using var scope = app.Services.CreateAsyncScope();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        Assert.True((await publishing.TransitionAsync(id, "publish", 1)).Succeeded);
        var original = Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var input = ArticleInput.From((await articles.FindAsync(id))!); input.Title = "Replacement title"; input.BodyMarkdown = "SCHEDULED replacement";
        await articles.SaveAsync(id, input); var due = clock.Now.UtcDateTime.AddHours(1);
        Assert.True((await publishing.ScheduleAsync(id, 2, due)).Succeeded);
        input = ArticleInput.From((await articles.FindAsync(id))!); input.BodyMarkdown = "PRIVATE preview edits"; await articles.SaveAsync(id, input);
        Assert.Contains("PRIVATE preview edits", await client.GetStringAsync($"/admin/posts/{id}/preview"));
        Assert.DoesNotContain("SCHEDULED replacement", await client.GetStringAsync("/"));
        clock.Now = new DateTimeOffset(due); await Run(app);
        var live = Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.Equal(original.Slug, live.Slug); Assert.Equal(original.PublishedAtUtc, live.PublishedAtUtc);
        foreach (var path in new[] { "/", "/articles/", "/tags/scheduled/", $"/posts/{original.Slug}/" })
        {
            var html = await client.GetStringAsync(path); Assert.Contains("SCHEDULED replacement", html); Assert.DoesNotContain("PRIVATE preview edits", html);
        }
    }

    [Fact]
    public async Task Due_batch_resolves_slug_collisions_and_one_manual_rebuild_completes_failed_schedules()
    {
        using var data = new TemporaryData(); var clock = new Clock(); var fault = new Fault();
        using var app = Application(data.Path, clock, fault); using var client = app.Client(); _ = await TestApplication.Setup(client);
        var first = await Draft(app); var second = await Draft(app); var due = clock.Now.UtcDateTime.AddHours(1);
        Assert.True((await Schedule(app, first, due)).Succeeded); Assert.True((await Schedule(app, second, due)).Succeeded);
        // A third post can take the same slug before either scheduled copy is committed.
        var third = await Draft(app);
        await using var scope = app.Services.CreateAsyncScope(); var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        Assert.True((await publishing.TransitionAsync(third, "publish", 1)).Succeeded);
        fault.Fail = true; clock.Now = new DateTimeOffset(due); await Run(app);
        await app.WithDatabase(async db =>
        {
            Assert.Equal(2, await db.PostSchedules.CountAsync());
            Assert.Equal(3, await db.Posts.Select(x => x.Slug).Distinct().CountAsync());
            Assert.Equal(1, await db.SiteGenerations.CountAsync(x => x.Status == GenerationStatus.Failed));
        });
        Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        fault.Fail = false;
        Assert.True((await publishing.RebuildAsync()).Succeeded);
        await app.WithDatabase(async db => Assert.Empty(await db.PostSchedules.ToArrayAsync()));
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        Assert.Equal(3, active.Articles.Length); Assert.Equal(3, active.Articles.Select(x => x.Slug).Distinct().Count());
        await Run(app); Assert.Equal(active.GenerationId, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
        // The new migration must leave SQLite's first-publication trigger intact.
        await app.WithDatabase(async db =>
        {
            var post = await db.Posts.SingleAsync(x => x.Id == first); post.Slug = "changed-after-publication";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Disabled_due_types_remain_private_visible_and_cancellable_then_resume_when_enabled()
    {
        using var data = new TemporaryData(); var clock = new Clock();
        using var app = Application(data.Path, clock); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var id = await Draft(app); var due = clock.Now.UtcDateTime.AddHours(1);
        Assert.True((await Schedule(app, id, due)).Succeeded);
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).EnableArticles = false; await db.SaveChangesAsync(); });
        clock.Now = new DateTimeOffset(due); await Run(app);
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
        Assert.Contains("This post type is disabled", await client.GetStringAsync("/admin/posts?scheduled=true"));
        await app.WithDatabase(async db => Assert.False((await db.PostSchedules.SingleAsync()).Committed));
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).EnableArticles = true; await db.SaveChangesAsync(); });
        clock.Now = clock.Now.AddMinutes(5); await Run(app);
        Assert.Equal(id, Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles).PostId);
        // Incomplete drafts cannot be scheduled, and rejection leaves no queued copy.
        await using var scope = app.Services.CreateAsyncScope();
        var empty = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync();
        Assert.False((await scope.ServiceProvider.GetRequiredService<PublishingService>().ScheduleAsync(empty.Id, 0, due.AddDays(1))).Succeeded);
        await app.WithDatabase(async db => Assert.Empty(await db.PostSchedules.ToArrayAsync()));
    }

    [Fact]
    public async Task Forms_save_before_scheduling_enforce_auth_csrf_conflicts_and_validation()
    {
        using var data = new TemporaryData(); var clock = new Clock();
        using var app = Application(data.Path, clock); using var author = app.Client(); using var visitor = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author); var id = await Draft(app);
        var path = $"/admin/posts/{id}/edit";
        var fields = new Dictionary<string, string> { ["Input.Version"] = "1", ["Input.Title"] = "Scheduled form", ["Input.BodyMarkdown"] = "Saved by scheduling", ["ScheduleLocal"] = "2030-01-10T14:00" };
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.PostAsync(path + "?handler=Schedule", new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsync(path + "?handler=Schedule", new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, path, fields, path + "?handler=Schedule")).StatusCode);
        await app.WithDatabase(async db =>
        {
            var snapshot = JsonSerializer.Deserialize<ArticleSnapshot>((await db.PostSchedules.SingleAsync()).SnapshotJson)!;
            Assert.Equal("Saved by scheduling", snapshot.BodyMarkdown);
        });
        Assert.Contains("Scheduled for", await author.GetStringAsync("/admin/posts?scheduled=true"));
        Assert.Equal(HttpStatusCode.Conflict, (await TestApplication.Submit(author, path, fields, path + "?handler=CancelSchedule")).StatusCode);
        fields["Input.Version"] = "3";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(author, path, fields, path + "?handler=CancelSchedule")).StatusCode);
        Assert.DoesNotContain("Scheduled for", await author.GetStringAsync("/admin/posts?scheduled=true"));
        await using var scope = app.Services.CreateAsyncScope(); var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        Assert.False((await publishing.ScheduleAsync(id, 4, clock.Now.UtcDateTime)).Succeeded);
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).EnableArticles = false; await db.SaveChangesAsync(); });
        Assert.False((await publishing.ScheduleAsync(id, 4, clock.Now.UtcDateTime.AddHours(1))).Succeeded);
    }
}
