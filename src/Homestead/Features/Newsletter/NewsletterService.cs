using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Newsletter;

public sealed class NewsletterService(HomesteadDbContext db, PublicationLock writer, ActiveSite active,
    IDataProtectionProvider protection, INewsletterEmailSender sender, TimeProvider clock, ILogger<NewsletterService> logger)
{
    private readonly IDataProtector credentials = protection.CreateProtector("Homestead.BrevoApiKey.v1");
    private readonly IDataProtector links = protection.CreateProtector("Homestead.NewsletterLinks.v1");
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public const string FailureMessage = "Email could not be sent. Check the Brevo API key, verified sender, sending quota, and connectivity.";
    private static string Encode(string text) => HtmlEncoder.Default.Encode(text);
    private static void Validate(object input) => Validator.ValidateObject(input, new ValidationContext(input), true);

    public async Task SaveAsync(NewsletterInput input, CancellationToken ct = default)
    {
        Validate(input);
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            var settings = await db.NewsletterSettings.SingleOrDefaultAsync(ct) ?? new();
            var key = input.ClearApiKey ? "" : settings.ProtectedApiKey;
            if (!string.IsNullOrWhiteSpace(input.ApiKey)) key = credentials.Protect(input.ApiKey.Trim());
            if (input.Enabled && (key.Length == 0 || string.IsNullOrWhiteSpace(input.SenderEmail) || string.IsNullOrWhiteSpace(input.SenderName)))
                throw new InvalidOperationException("Enter a Brevo API key, verified sender email, and sender name before enabling newsletters.");
            if (!settings.Enabled && input.Enabled)
            {
                // Establish a baseline before enabling; existing posts never become a mailing backlog.
                if (active.Current is { } manifest) await CaptureLockedAsync(manifest, ct);
                settings.EnabledAtUtc = Now;
            }
            settings.Enabled = input.Enabled; settings.ProtectedApiKey = key;
            settings.SenderEmail = input.SenderEmail?.Trim() ?? ""; settings.SenderName = input.SenderName?.Trim() ?? "";
            settings.LastError = null;
            if (db.Entry(settings).State == EntityState.Detached) db.NewsletterSettings.Add(settings);
            (await db.SiteSettings.SingleAsync(ct)).EnableNewsletter = input.Enabled;
            if (!input.Enabled)
                foreach (var delivery in await db.NewsletterDeliveries.Where(x => x.Status == NewsletterDeliveryStatus.Pending).ToListAsync(ct))
                    delivery.Status = NewsletterDeliveryStatus.Cancelled;
            await db.SaveChangesAsync(ct);
        }
        finally { writer.Semaphore.Release(); }
    }

    // Called after pointer activation with the publication lock already held. Also reconciled by the worker on restart.
    public async Task CaptureLockedAsync(GenerationManifest manifest, CancellationToken ct = default)
    {
        var ids = await db.NewsletterPosts.Select(x => x.PostId).ToListAsync(ct);
        foreach (var post in manifest.Articles.Where(x => !ids.Contains(x.PostId)))
            db.NewsletterPosts.Add(new() { PostId = post.PostId, ActivatedAtUtc = manifest.ActivatedAtUtc, SnapshotJson = JsonSerializer.Serialize(post) });
        await db.SaveChangesAsync(ct);
    }

    private bool Available(NewsletterSettings? settings) => settings?.Enabled == true && active.Current?.Settings.EnableNewsletter == true && active.IsHealthy;
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) => Available(await db.NewsletterSettings.AsNoTracking().SingleOrDefaultAsync(ct));

    public async Task RequestSubscriptionAsync(SubscriptionInput input, CancellationToken ct = default)
    {
        Validate(input);
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            var settings = await db.NewsletterSettings.SingleOrDefaultAsync(ct);
            if (!Available(settings)) throw new InvalidOperationException("Newsletter subscriptions are currently unavailable.");
            var email = input.Email.Trim().ToLowerInvariant();
            var subscriber = await db.NewsletterSubscribers.SingleOrDefaultAsync(x => x.Email == email, ct) ?? new() { Email = email };
            // Generic success for repeated addresses; requests cannot change confirmed preferences.
            if (subscriber.LastConfirmationAtUtc is { } last && Now < last.AddMinutes(15)) return;
            subscriber.PendingToken = Guid.NewGuid(); subscriber.PendingFrequency = input.Frequency; subscriber.PendingPostTypes = input.TypeMask;
            subscriber.ConfirmationExpiresAtUtc = Now.AddHours(24); subscriber.LastConfirmationAtUtc = Now;
            if (db.Entry(subscriber).State == EntityState.Detached) db.NewsletterSubscribers.Add(subscriber);
            await db.SaveChangesAsync(ct);
            var token = links.Protect($"confirm|{subscriber.Id:D}|{subscriber.PendingToken:D}");
            var url = DiscoveryFiles.Absolute(active.Current!.Settings, "/newsletter/?token=" + Uri.EscapeDataString(token));
            var title = active.Current.Settings.Title;
            try
            {
                await SendAsync(settings!, new(Guid.NewGuid(), email, "Confirm your newsletter subscription",
                    $"<html><body><p>Confirm your subscription to {Encode(title)}.</p><p><a href=\"{Encode(url)}\">Confirm subscription</a></p><p>This link expires in 24 hours. If you did not request this, ignore this email.</p></body></html>",
                    $"Confirm your subscription to {title}:\n{url}\nThis link expires in 24 hours. If you did not request this, ignore this email."), ct);
                settings!.LastError = null; settings.LastSuccessAtUtc = Now;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                settings!.LastError = FailureMessage;
                logger.LogWarning("Newsletter confirmation failed ({FailureType}).", ex.GetType().Name);
                // Permit a later request to retry without leaking delivery status or address membership.
                subscriber.LastConfirmationAtUtc = Now.AddMinutes(-13);
            }
            await db.SaveChangesAsync(ct);
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task<NewsletterSubscriber?> ResolveAsync(string? token, bool confirmation, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        try
        {
            var parts = links.Unprotect(token).Split('|');
            if (parts.Length != 3 || parts[0] != (confirmation ? "confirm" : "manage") ||
                !Guid.TryParse(parts[1], out var id) || !Guid.TryParse(parts[2], out var version)) return null;
            var subscriber = await db.NewsletterSubscribers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return subscriber is not null && (confirmation ? subscriber.PendingToken == version && subscriber.ConfirmationExpiresAtUtc > Now :
                subscriber.TokenVersion == version && subscriber.ConfirmedAtUtc is not null) ? subscriber : null;
        }
        catch (CryptographicException) { return null; }
    }

    public string ManagementToken(NewsletterSubscriber subscriber) => links.Protect($"manage|{subscriber.Id:D}|{subscriber.TokenVersion:D}");

    public async Task<string?> ConfirmAsync(string token, CancellationToken ct = default)
    {
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            if (!Available(await db.NewsletterSettings.SingleOrDefaultAsync(ct))) return null;
            if (await ResolveAsync(token, true, ct) is not { } found) return null;
            var subscriber = await db.NewsletterSubscribers.SingleAsync(x => x.Id == found.Id, ct);
            subscriber.Subscribed = true; subscriber.Frequency = subscriber.PendingFrequency; subscriber.PostTypes = subscriber.PendingPostTypes;
            subscriber.ConfirmedAtUtc = Now; subscriber.NextDigestAtUtc = Now.AddDays(7); subscriber.TokenVersion = Guid.NewGuid();
            subscriber.PendingToken = Guid.Empty; subscriber.ConfirmationExpiresAtUtc = null;
            await CancelPendingAsync(subscriber.Id, ct);
            await db.SaveChangesAsync(ct);
            return ManagementToken(subscriber);
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task<bool> ManageAsync(string token, SubscriptionInput? input, CancellationToken ct = default)
    {
        if (input is not null) Validate(input);
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            if (await ResolveAsync(token, false, ct) is not { } found) return false;
            var subscriber = await db.NewsletterSubscribers.SingleAsync(x => x.Id == found.Id, ct);
            subscriber.Subscribed = input is not null;
            if (input is not null)
            {
                subscriber.Frequency = input.Frequency; subscriber.PostTypes = input.TypeMask;
                // Start the new schedule now rather than replaying old posts for a changed preference.
                subscriber.ConfirmedAtUtc = Now; subscriber.NextDigestAtUtc = Now.AddDays(7);
            }
            subscriber.PendingToken = Guid.Empty; subscriber.ConfirmationExpiresAtUtc = null;
            await CancelPendingAsync(subscriber.Id, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally { writer.Semaphore.Release(); }
    }

    private async Task CancelPendingAsync(Guid id, CancellationToken ct)
    {
        foreach (var delivery in await db.NewsletterDeliveries.Where(x => x.SubscriberId == id && x.Status == NewsletterDeliveryStatus.Pending).ToListAsync(ct))
            delivery.Status = NewsletterDeliveryStatus.Cancelled;
    }

    private Task SendAsync(NewsletterSettings settings, NewsletterEmail email, CancellationToken ct) =>
        sender.SendAsync(credentials.Unprotect(settings.ProtectedApiKey), settings.SenderEmail, settings.SenderName, email, ct);

    public async Task RunAsync(CancellationToken ct = default)
    {
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            db.ChangeTracker.Clear();
            if (active.Current is not { } manifest || !manifest.Settings.EnableNewsletter || !active.IsHealthy) return;
            await CaptureLockedAsync(manifest, ct);
            var settings = await db.NewsletterSettings.SingleOrDefaultAsync(ct);
            if (!Available(settings)) return;
            var live = manifest.Articles.Select(x => x.PostId).ToHashSet();
            var posts = (await db.NewsletterPosts.ToListAsync(ct)).Where(x => live.Contains(x.PostId) && x.ActivatedAtUtc > settings!.EnabledAtUtc).ToArray();
            var deliveries = await db.NewsletterDeliveries.Select(x => new { x.SubscriberId, x.Key }).ToListAsync(ct);
            foreach (var subscriber in await db.NewsletterSubscribers.Where(x => x.Subscribed).ToListAsync(ct))
            {
                var selected = posts.Where(x => x.ActivatedAtUtc > subscriber.ConfirmedAtUtc &&
                    (subscriber.PostTypes & (1 << (int)JsonSerializer.Deserialize<ArticleSnapshot>(x.SnapshotJson)!.Type)) != 0).ToArray();
                void Queue(string key, IEnumerable<NewsletterPost> items)
                {
                    if (!deliveries.Any(x => x.SubscriberId == subscriber.Id && x.Key == key))
                        db.NewsletterDeliveries.Add(new()
                        {
                            SubscriberId = subscriber.Id,
                            Key = key,
                            PostIdsJson = JsonSerializer.Serialize(items.Select(x => x.PostId)),
                            NextAttemptAtUtc = Now
                        });
                }
                if (subscriber.Frequency == NewsletterFrequency.Instant)
                    foreach (var post in selected) Queue("post:" + post.PostId.ToString("D"), [post]);
                else if (subscriber.NextDigestAtUtc is { } due && Now >= due)
                {
                    // Include all new posts since the previous window, including downtime, in one summary.
                    var window = selected.Where(x => x.ActivatedAtUtc > due.AddDays(-7)).ToArray();
                    if (window.Length > 0) Queue("week:" + due.Ticks, window);
                    subscriber.NextDigestAtUtc = Now.AddDays(7);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        finally { writer.Semaphore.Release(); }

        var pending = await db.NewsletterDeliveries.AsNoTracking().Where(x => x.Status == NewsletterDeliveryStatus.Pending && x.NextAttemptAtUtc <= Now)
            .OrderBy(x => x.NextAttemptAtUtc).Take(10).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in pending)
        {
            // Release between recipients so an author can publish or a reader can unsubscribe.
            await writer.Semaphore.WaitAsync(ct);
            try { db.ChangeTracker.Clear(); await DeliverLockedAsync(id, ct); }
            finally { writer.Semaphore.Release(); }
        }
    }

    private async Task DeliverLockedAsync(Guid id, CancellationToken ct)
    {
        var settings = await db.NewsletterSettings.SingleOrDefaultAsync(ct);
        if (!Available(settings)) return;
        var delivery = await db.NewsletterDeliveries.SingleAsync(x => x.Id == id, ct);
        if (delivery.Status != NewsletterDeliveryStatus.Pending || delivery.NextAttemptAtUtc > Now) return;
        var subscriber = await db.NewsletterSubscribers.SingleAsync(x => x.Id == delivery.SubscriberId, ct);
        var ids = JsonSerializer.Deserialize<Guid[]>(delivery.PostIdsJson)!;
        var live = active.Current!.Articles.Select(x => x.PostId).ToHashSet();
        var posts = (await db.NewsletterPosts.Where(x => ids.Contains(x.PostId)).ToListAsync(ct))
            .Where(x => live.Contains(x.PostId)).Select(x => JsonSerializer.Deserialize<ArticleSnapshot>(x.SnapshotJson)!)
            .Where(x => (subscriber.PostTypes & (1 << (int)x.Type)) != 0).OrderByDescending(x => x.PublishedAtUtc).ThenBy(x => x.PostId).ToArray();
        if (!subscriber.Subscribed || posts.Length == 0 || delivery.Attempts > 0 && posts.Length != ids.Length)
        { delivery.Status = NewsletterDeliveryStatus.Cancelled; await db.SaveChangesAsync(ct); return; }
        var site = active.Current.Settings;
        var manage = DiscoveryFiles.Absolute(site, "/newsletter/?manage=" + Uri.EscapeDataString(ManagementToken(subscriber)));
        var title = delivery.Key.StartsWith("week:", StringComparison.Ordinal) ? $"{site.Title}: weekly summary" : $"{site.Title}: {posts[0].DisplayTitle}";
        var text = title + "\n\n" + string.Join("\n\n", posts.Select(post => $"{post.DisplayTitle}\n{DiscoveryFiles.Description(post)}\n{DiscoveryFiles.Absolute(site, $"/posts/{post.Slug}/")}")) +
            "\n\nPreferences or unsubscribe:\n" + manage;
        var html = "<html><body><h1>" + Encode(title) + "</h1>" + string.Join("", posts.Select(post =>
            $"<h2><a href=\"{Encode(DiscoveryFiles.Absolute(site, $"/posts/{post.Slug}/"))}\">{Encode(post.DisplayTitle)}</a></h2><p>{Encode(DiscoveryFiles.Description(post))}</p>")) +
            $"<p><a href=\"{Encode(manage)}\">Preferences or unsubscribe</a></p></body></html>";
        // Freeze the exact request content before the first attempt, including its management link.
        delivery.Subject ??= title.Replace('\r', ' ').Replace('\n', ' '); delivery.Html ??= html; delivery.Text ??= text;
        delivery.Attempts++; delivery.NextAttemptAtUtc = Now.AddMinutes(2);
        await db.SaveChangesAsync(ct); // Retain the same provider idempotency key across crashes/retries.
        try
        {
            await SendAsync(settings!, new(delivery.Id, subscriber.Email, delivery.Subject, delivery.Html, delivery.Text), ct);
            delivery.Status = NewsletterDeliveryStatus.Sent; delivery.SentAtUtc = Now;
            settings!.LastSuccessAtUtc = Now; settings.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            settings!.LastError = FailureMessage;
            delivery.NextAttemptAtUtc = Now.AddMinutes(Math.Min(60, 2 * Math.Pow(2, Math.Min(delivery.Attempts - 1, 5))));
            logger.LogWarning("Newsletter delivery failed ({FailureType}).", ex.GetType().Name);
        }
        await db.SaveChangesAsync(ct);
    }
}
