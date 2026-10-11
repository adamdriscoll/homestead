using System.Globalization;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Publishing;

public sealed partial class PublishingService
{
    // Resolve once when scheduling. Later timezone changes cannot move a saved instant.
    public static DateTime ResolveScheduleTime(DateTime local, string timeZone, string? utcOffset = null)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        if (zone.IsInvalidTime(local)) throw new InvalidOperationException("This time does not exist because the clocks move forward. Choose another time.");
        if (string.IsNullOrWhiteSpace(utcOffset))
        {
            if (zone.IsAmbiguousTime(local)) throw new InvalidOperationException("This time occurs twice when the clocks move back. Enter its UTC offset, such as -05:00 or -06:00.");
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }
        if (utcOffset.Length != 6 || utcOffset[0] is not ('+' or '-') ||
            !TimeSpan.TryParseExact(utcOffset[1..], "hh\\:mm", CultureInfo.InvariantCulture, out var offset))
            throw new InvalidOperationException("Enter a UTC offset as +HH:mm or -HH:mm.");
        if (utcOffset[0] == '-') offset = -offset;
        var valid = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        if (!valid.Contains(offset)) throw new InvalidOperationException("The UTC offset does not match this time in the site's timezone.");
        return new DateTimeOffset(local, offset).UtcDateTime;
    }

    public async Task<PublicationResult> ScheduleAsync(Guid id, long version, DateTime local, string? utcOffset = null)
    {
        await writer.Semaphore.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var post = await articles.FindAsync(id) ?? throw new KeyNotFoundException();
            if (post.Draft!.Version != version) throw new DraftConflictException();
            if (post.Schedule is { Committed: true }) return new(false, "Publication has already been committed. Use Publish, Unpublish, or Rebuild site to manage the pending change.");
            var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
            if (!settings.IsPostTypeEnabled(post.Type)) return new(false, "Enable this post type in Settings before scheduling.");
            if (PostValidation.PublicationError(post) is { } error) return new(false, error);
            var due = ResolveScheduleTime(local, settings.TimeZone, utcOffset);
            if (due <= clock.GetUtcNow().UtcDateTime) return new(false, "Choose a future publication time.");
            post.Draft.Version++;
            var snapshot = await PrepareSnapshotAsync(post, due);
            post.Schedule ??= new PostSchedule();
            post.Schedule.PublishAtUtc = due;
            post.Schedule.NextAttemptAtUtc = due;
            post.Schedule.SnapshotJson = JsonSerializer.Serialize(snapshot);
            post.Schedule.LastAttemptAtUtc = null;
            post.Schedule.Error = null;
            post.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            return new(true, "Post scheduled. This saved copy will publish at the selected time; later draft edits remain private until you replace the schedule.");
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task<PublicationResult> CancelScheduleAsync(Guid id, long version)
    {
        await writer.Semaphore.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var post = await articles.FindAsync(id) ?? throw new KeyNotFoundException();
            if (post.Draft!.Version != version) throw new DraftConflictException();
            if (post.Schedule is { Committed: true }) return new(false, "Publication has already been committed. Use Unpublish to withdraw it, or Publish to replace it.");
            if (post.Schedule is { } schedule)
            {
                db.PostSchedules.Remove(schedule); post.Schedule = null;
                post.Draft.Version++; post.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync();
            }
            return new(true, "Schedule cancelled. Your draft and current public post are unchanged.");
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task RunSchedulesAsync(CancellationToken ct = default)
    {
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            db.ChangeTracker.Clear();
            var now = clock.GetUtcNow().UtcDateTime;
            var schedules = await db.PostSchedules.Include(x => x.Post).ThenInclude(x => x.PublishedSnapshot)
                .Where(x => x.PublishAtUtc <= now).OrderBy(x => x.PublishAtUtc).ThenBy(x => x.PostId).ToArrayAsync(ct);
            var settings = await db.SiteSettings.AsNoTracking().SingleAsync(ct);
            var attempted = new List<PostSchedule>();
            foreach (var schedule in schedules)
            {
                ct.ThrowIfCancellationRequested();
                if (IsScheduleActive(schedule)) { db.PostSchedules.Remove(schedule); continue; }
                if (schedule.NextAttemptAtUtc > now) continue;
                schedule.LastAttemptAtUtc = now;
                schedule.NextAttemptAtUtc = now.AddMinutes(5);
                if (!settings.IsPostTypeEnabled(schedule.Post.Type))
                {
                    schedule.Error = "This post type is disabled. Enable it in Settings; the schedule will retry.";
                    continue;
                }
                if (!schedule.Committed)
                {
                    var snapshot = JsonSerializer.Deserialize<ArticleSnapshot>(schedule.SnapshotJson) ?? throw new InvalidOperationException("Invalid scheduled snapshot.");
                    // Another post can claim the candidate slug while this copy waits privately.
                    snapshot = snapshot with { Slug = schedule.Post.Slug ?? await UniqueSlugAsync(snapshot.Slug, schedule.PostId) };
                    CommitSnapshot(schedule.Post, snapshot);
                    schedule.SnapshotJson = JsonSerializer.Serialize(snapshot);
                    schedule.Committed = true;
                }
                schedule.Error = "Scheduled publication is pending. The worker will retry; Rebuild site can also apply the saved change.";
                attempted.Add(schedule);
            }
            // Persist desired snapshots and retry state together before starting any build.
            await db.SaveChangesAsync(ct);
            if (attempted.Count == 0) return;
            var result = await BuildLockedAsync();
            foreach (var schedule in attempted)
            {
                if (db.Entry(schedule).State == EntityState.Detached) continue;
                if (IsScheduleActive(schedule)) db.PostSchedules.Remove(schedule);
                else schedule.Error = result.Message + " Scheduled publication will retry in five minutes.";
            }
            await db.SaveChangesAsync(ct);
        }
        finally { writer.Semaphore.Release(); }
    }

    private bool IsScheduleActive(PostSchedule schedule) => schedule.Committed && active.RecoveryError is null &&
        active.Current?.Articles.SingleOrDefault(x => x.PostId == schedule.PostId) is { } live &&
        JsonSerializer.Serialize(live) == schedule.SnapshotJson;

    private async Task CompleteActiveSchedulesLockedAsync()
    {
        foreach (var schedule in await db.PostSchedules.Include(x => x.Post).Where(x => x.Committed).ToArrayAsync())
            if (IsScheduleActive(schedule))
            {
                db.PostSchedules.Remove(schedule);
                schedule.Post.Schedule = null;
            }
    }

    private async Task<string> UniqueSlugAsync(string root, Guid id)
    {
        var slug = root; var suffix = 2;
        while (await db.Posts.AnyAsync(x => x.Slug == slug && x.Id != id) || db.Posts.Local.Any(x => x.Slug == slug && x.Id != id)) slug = $"{root}-{suffix++}";
        return slug;
    }

    private async Task<ArticleSnapshot> PrepareSnapshotAsync(Post post, DateTime now)
    {
        var title = post.Type == PostType.Review ? post.Review!.Subject : post.Type == PostType.Checkin && string.IsNullOrWhiteSpace(post.Draft!.Title)
            ? $"checkin-{post.Draft.LocationName}" : post.Draft!.Title;
        var slug = post.Slug ?? await UniqueSlugAsync(string.IsNullOrWhiteSpace(title)
            ? $"{(post.Type == PostType.Microblog ? "note" : "gallery")}-{now:yyyy-MM-dd}-{post.Id.ToString("N")[..8]}" : Slugs.Create(title), post.Id);
        var snapshot = ArticleService.Snapshot(post, slug, now);
        return snapshot with { Media = markdown.ReferencedMedia(snapshot) };
    }

    private static void CommitSnapshot(Post post, ArticleSnapshot snapshot)
    {
        post.Slug = snapshot.Slug; post.FirstPublishedAtUtc ??= snapshot.PublishedAtUtc; post.LastPublishedAtUtc = snapshot.ModifiedAtUtc;
        post.Status = PostStatus.Published;
        post.PublishedSnapshot ??= new PostPublishedSnapshot();
        post.PublishedSnapshot.SnapshotJson = JsonSerializer.Serialize(snapshot);
        post.PublishedSnapshot.PublishedAtUtc = snapshot.ModifiedAtUtc; post.PublishedSnapshot.IsActive = true;
    }
}
