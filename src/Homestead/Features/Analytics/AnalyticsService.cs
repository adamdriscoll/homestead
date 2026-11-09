using System.Text.RegularExpressions;
using DeviceDetectorNET.Parser;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Analytics;

// The public request path reads only this cached opt-in flag; SQLite never determines
// which files are served. Counts and configuration remain private, outside manifests.
public sealed class AnalyticsService(IServiceScopeFactory scopes, PublicationLock writer, ILogger<AnalyticsService> logger)
{
    private bool enabled;
    public bool Enabled => Volatile.Read(ref enabled);

    public async Task InitializeAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HomesteadDbContext>();
        Volatile.Write(ref enabled, await db.AnalyticsSettings.AnyAsync(x => x.Enabled));
    }

    public async Task SetEnabledAsync(bool value, CancellationToken ct = default)
    {
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HomesteadDbContext>();
            var settings = await db.AnalyticsSettings.SingleOrDefaultAsync(ct);
            if (settings is null) { settings = new(); db.AnalyticsSettings.Add(settings); }
            settings.Enabled = value;
            await db.SaveChangesAsync(ct);
            Volatile.Write(ref enabled, value);
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task RecordAsync(HttpContext context, string path)
    {
        if (!Enabled || !HttpMethods.IsGet(context.Request.Method) || context.User.Identity?.IsAuthenticated == true) return;
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (!IsHuman(userAgent)) return;
        var ct = context.RequestAborted;
        try
        {
            await writer.Semaphore.WaitAsync(ct);
            try
            {
                // Disabling waits for any current increment and prevents queued increments.
                if (!Enabled) return;
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HomesteadDbContext>();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "PageViews" ("Path", "Hits") VALUES ({path}, 1)
                    ON CONFLICT ("Path") DO UPDATE SET "Hits" = "Hits" + 1;
                    """, ct);
            }
            finally { writer.Semaphore.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            // A statistics failure must never replace a successfully served static page.
            logger.LogWarning(ex, "Could not record a public page view.");
        }
    }

    internal static bool IsHuman(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent) || userAgent.Length > 2048) return false;
        // Supplement the parser's named crawlers with obvious browser automation and
        // command-line clients. Browser-like agents that hide their identity can pass.
        string[] automation = ["HeadlessChrome", "PhantomJS", "Playwright", "Puppeteer", "Selenium", "curl/", "Wget/", "python-", "httpclient", "Go-http-client", "node-fetch", "undici"];
        if (automation.Any(x => userAgent.Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            var parser = new BotParser();
            parser.SetUserAgent(userAgent);
            return !parser.Parse().Success;
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
