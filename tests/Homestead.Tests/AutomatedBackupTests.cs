using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class AutomatedBackupTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Uploads : IBackupUploader
    {
        public bool Fail { get; set; }
        public List<(string Key, byte[] Bytes)> Files { get; } = [];
        public async Task UploadAsync(BackupSettings settings, string accessKey, string secretKey, string objectKey, Stream archive, CancellationToken ct)
        {
            Assert.Equal("private-access", accessKey); Assert.Equal("private-secret", secretKey);
            if (Fail) throw new IOException("Provider message with private-secret must never appear in status.");
            using var copy = new MemoryStream(); await archive.CopyToAsync(copy, ct);
            Files.Add((objectKey, copy.ToArray()));
        }
    }

    private static TestApplication Application(TemporaryData data, Uploads uploads, Clock clock) => new(data.Path, configureServices: services =>
    {
        var worker = services.Single(x => x.ImplementationType == typeof(AutomatedBackupWorker));
        services.Remove(worker);
        services.AddSingleton<IBackupUploader>(uploads); services.AddSingleton<TimeProvider>(clock);
    });

    private static BackupInput Input(BackupFrequency frequency = BackupFrequency.BeforePublication) => new()
    {
        Enabled = true,
        Frequency = frequency,
        Bucket = "private-backups",
        AccessKey = "private-access",
        SecretKey = "private-secret"
    };

    private static async Task<AutomatedBackupResult> Run(TestApplication app, bool scheduled = true)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AutomatedBackupService>().RunAsync(scheduled);
    }

    [Theory]
    [InlineData(BackupFrequency.Daily, 1)]
    [InlineData(BackupFrequency.Weekly, 7)]
    public async Task Schedule_is_durable_retries_failures_and_cleans_archives(BackupFrequency frequency, int days)
    {
        using var data = new TemporaryData(); var uploads = new Uploads(); var clock = new Clock();
        using (var app = Application(data, uploads, clock))
        {
            using var client = app.Client(); _ = await TestApplication.Setup(client);
            await using (var scope = app.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AutomatedBackupService>().SaveAsync(Input(frequency));
            var concurrent = await Task.WhenAll(Run(app), Run(app));
            Assert.All(concurrent, result => Assert.True(result.Succeeded)); Assert.Single(uploads.Files);
            Assert.True((await Run(app)).Succeeded); Assert.Single(uploads.Files);
            clock.Now += TimeSpan.FromDays(days) - TimeSpan.FromSeconds(1);
            await Run(app); Assert.Single(uploads.Files);
        }
        using var restarted = Application(data, uploads, clock); using var visitor = restarted.Client();
        await Run(restarted); Assert.Single(uploads.Files);
        clock.Now += TimeSpan.FromSeconds(1); uploads.Fail = true;
        Assert.False((await Run(restarted)).Succeeded); Assert.Single(uploads.Files);
        await restarted.WithDatabase(async db =>
        {
            var settings = await db.BackupSettings.SingleAsync();
            Assert.DoesNotContain("private-secret", settings.LastError!);
            Assert.Equal(clock.Now.UtcDateTime - TimeSpan.FromDays(days), settings.LastSuccessAtUtc);
        });
        uploads.Fail = false; await Run(restarted); Assert.Single(uploads.Files);
        clock.Now += TimeSpan.FromMinutes(15);
        Assert.True((await Run(restarted)).Succeeded); Assert.Equal(2, uploads.Files.Count);
        Assert.NotEqual(uploads.Files[0].Key, uploads.Files[1].Key);
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "backups")));
    }

    [Fact]
    public async Task Before_publication_failure_preserves_active_site_and_success_creates_restorable_full_backup()
    {
        using var data = new TemporaryData(); var uploads = new Uploads(); var clock = new Clock();
        using var app = Application(data, uploads, clock); using var client = app.Client();
        _ = await TestApplication.Setup(client); _ = await TestApplication.Login(client);
        var previous = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AutomatedBackupService>();
        await service.SaveAsync(Input());
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var post = await articles.CreateAsync();
        var version = await articles.SaveAsync(post.Id, new() { Title = "Protected publication", BodyMarkdown = "Private draft" });
        uploads.Fail = true;
        var publication = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var result = await publication.TransitionAsync(post.Id, "publish", version);
        Assert.False(result.Succeeded); Assert.Contains("saved changes are pending", result.Message);
        Assert.Equal(previous, app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId);
        await app.WithDatabase(async db => Assert.True((await db.PublishedSnapshots.SingleAsync()).IsActive));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/posts/protected-publication/")).StatusCode);
        uploads.Fail = false;
        Assert.True((await publication.RebuildAsync()).Succeeded); Assert.Single(uploads.Files);
        Assert.Contains("Private draft", await client.GetStringAsync("/posts/protected-publication/"));
        await Run(app); Assert.Single(uploads.Files); // Publication cadence never runs in the scheduler.
        using var zip = new ZipArchive(new MemoryStream(uploads.Files[0].Bytes));
        Assert.NotNull(zip.GetEntry("homestead.db")); Assert.Contains(zip.Entries, x => x.FullName.StartsWith("keys/", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, x => x.FullName.StartsWith("site/", StringComparison.Ordinal));
        var archive = Path.Combine(data.Path, "test-backup.zip"); await File.WriteAllBytesAsync(archive, uploads.Files[0].Bytes);
        using var restoredData = new TemporaryData();
        await BackupService.RestoreAsync(archive, restoredData.Path);
        using var restored = Application(restoredData, uploads, clock); using var restoredClient = restored.Client();
        Assert.Contains("Private draft", await restoredClient.GetStringAsync("/posts/protected-publication/"));
        Assert.Equal(2, uploads.Files.Count); // Restore startup rebuild uses the restored protected credentials/key ring.
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "backups")));
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(app.Services.GetRequiredService<ActiveSite>().Current));
        Assert.DoesNotContain("ProtectedSecretKey", JsonSerializer.Serialize(app.Services.GetRequiredService<ActiveSite>().Current));
    }

    [Fact]
    public async Task Backup_form_is_authenticated_validated_and_keeps_saved_credentials_private()
    {
        // HttpClient's cookie container checks persistent-cookie expiry against wall-clock time.
        using var data = new TemporaryData(); var uploads = new Uploads(); var clock = new Clock { Now = DateTimeOffset.UtcNow };
        using var app = Application(data, uploads, clock); using var client = app.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin/settings")).StatusCode);
        var setup = await TestApplication.Setup(client); var login = await TestApplication.Login(client);
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/admin", login.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/settings")).StatusCode);
        var invalid = await client.PostAsync("/admin/settings?handler=Backup", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var fields = new Dictionary<string, string>
        {
            ["Backup.Enabled"] = "true",
            ["Backup.Frequency"] = "BeforePublication",
            ["Backup.Bucket"] = "private-backups",
            ["Backup.AccessKey"] = "private-access",
            ["Backup.SecretKey"] = "private-secret"
        };
        var response = await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Backup");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/admin/settings#backup", response.Headers.Location!.ToString());
        var html = await client.GetStringAsync("/admin/settings");
        Assert.Contains("id=\"backup\"", html); Assert.DoesNotContain("private-secret", html); Assert.DoesNotContain("private-access", html);
        await app.WithDatabase(async db =>
        {
            var settings = await db.BackupSettings.SingleAsync();
            Assert.DoesNotContain("private-secret", settings.ProtectedSecretKey); Assert.DoesNotContain("private-access", settings.ProtectedAccessKey);
        });
        fields.Remove("Backup.AccessKey"); fields.Remove("Backup.SecretKey");
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Backup")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", new(), "/admin/settings?handler=BackupNow")).StatusCode);
        Assert.Single(uploads.Files);
        fields["Backup.Frequency"] = "invalid-frequency";
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Backup")).StatusCode);
        fields["Backup.Frequency"] = "BeforePublication";
        foreach (var endpoint in new[] { "http://storage.example.test", "https://user:password@example.test", "https://storage.example.test/?secret=private-secret" })
        {
            fields["Backup.Endpoint"] = endpoint;
            Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Backup")).StatusCode);
            await app.WithDatabase(async db => Assert.Empty((await db.BackupSettings.SingleAsync()).Endpoint));
        }
        fields.Remove("Backup.Endpoint"); fields["Backup.ClearCredentials"] = "true";
        Assert.Equal(HttpStatusCode.OK, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Backup")).StatusCode);
        fields["Backup.Enabled"] = "false";
        Assert.Equal(HttpStatusCode.Redirect, (await TestApplication.Submit(client, "/admin/settings", fields, "/admin/settings?handler=Backup")).StatusCode);
        await Run(app, false); Assert.Single(uploads.Files);
        await app.WithDatabase(async db => Assert.Empty((await db.BackupSettings.SingleAsync()).ProtectedSecretKey));
    }
}
