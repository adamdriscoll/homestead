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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public async Task Activation_failure_leaves_pointer_and_public_output_unchanged_and_retry_is_safe()
    {
        using var data = new TemporaryData(); var fault = new ActivationFault();
        using var app = new TestApplication(data.Path, configureServices: services =>
            services.AddSingleton<IGenerationActivation>(provider => new FailingActivation(
                new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), fault)));
        using var client = app.Client(); _ = await TestApplication.Setup(client);
        var pointer = File.ReadAllBytes(Path.Combine(data.Path, "site", "current.json")); var home = await client.GetStringAsync("/");
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var post = await articles.CreateAsync(); await articles.SaveAsync(post.Id, new() { Title = "Atomic publication", BodyMarkdown = "Committed content" });
        fault.Fail = true;
        Assert.False((await publishing.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(data.Path, "site", "current.json")));
        Assert.Equal(home, await client.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/posts/atomic-publication/")).StatusCode);
        Assert.Single(Directory.GetDirectories(Path.Combine(data.Path, "site", "builds")));
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "site"), "*.tmp"));
        fault.Fail = false;
        Assert.True((await publishing.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        Assert.True((await publishing.TransitionAsync(post.Id, "publish", 1)).Succeeded);
        await app.WithDatabase(async db => Assert.Single(await db.Posts.ToArrayAsync()));
        Assert.Single(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    [Fact]
    public async Task Crash_after_activation_keeps_site_live_and_startup_reconciles_record()
    {
        using var data = new TemporaryData(); var fault = new SaveFault(); Guid generation;
        using (var app = new TestApplication(data.Path, configureServices: services =>
            services.AddDbContext<HomesteadDbContext>((_, options) => options.AddInterceptors(fault))))
        using (var client = app.Client())
        {
            _ = await TestApplication.Setup(client);
            fault.AfterActivation = true;
            await using var scope = app.Services.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().RebuildAsync()).Succeeded);
            generation = app.Services.GetRequiredService<ActiveSite>().Current!.GenerationId;
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        }
        using var fresh = new TestApplication(data.Path, setupToken: null); using var visitor = fresh.Client();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/health/ready")).StatusCode);
        await fresh.WithDatabase(async db => Assert.Equal(GenerationStatus.Succeeded, (await db.SiteGenerations.SingleAsync(x => x.Id == generation)).Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_database_failure_removes_original_and_does_not_create_public_media(bool retainOriginal)
    {
        using var data = new TemporaryData(); var fault = new SaveFault();
        using var app = new TestApplication(data.Path, configureServices: services =>
            services.AddDbContext<HomesteadDbContext>((_, options) => options.AddInterceptors(fault)));
        using var client = app.Client(); _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var post = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync();
        fault.Media = true;
        await using var bytes = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png")));
        var image = new FormFile(bytes, 0, bytes.Length, "image", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        await Assert.ThrowsAsync<IOException>(() => scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(post.Id, image, "photo", retainOriginal: retainOriginal));
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "media"), "*", SearchOption.AllDirectories));
        await app.WithDatabase(async db => Assert.Empty(await db.MediaAssets.ToArrayAsync()));
        Assert.Empty(app.Services.GetRequiredService<ActiveSite>().Current!.Articles);
    }

    [Fact]
    public async Task Backup_and_upload_share_lock_and_produce_a_restorable_consistent_capture()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); using var output = new TemporaryData();
        using var app = new TestApplication(data.Path); using var client = app.Client(); _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope(); await using var uploadScope = app.Services.CreateAsyncScope();
        var post = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync();
        await using var bytes = new MemoryStream(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Homestead/wwwroot/icon-192.png")));
        var image = new FormFile(bytes, 0, bytes.Length, "image", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        var writer = app.Services.GetRequiredService<PublicationLock>(); await writer.Semaphore.WaitAsync();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync();
        var upload = uploadScope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(post.Id, image, "photo");
        Assert.False(backup.IsCompleted); Assert.False(upload.IsCompleted); writer.Semaphore.Release();
        await using var archive = await backup; _ = await upload;
        Directory.CreateDirectory(output.Path); var path = Path.Combine(output.Path, "backup.zip");
        await using (var file = File.Create(path)) await archive.CopyToAsync(file);
        await BackupService.RestoreAsync(path, restored.Path);
        using var fresh = new TestApplication(restored.Path, setupToken: null); using var visitor = fresh.Client();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData(".tif", "image/tiff")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".webp", "image/webp")]
    public async Task Tiff_signatures_are_rejected_before_decoder_even_when_disguised(string extension, string mime)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client); await using var scope = app.Services.CreateAsyncScope();
        var post = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync();
        // BigTIFF header with adversarial IFD count; no TIFF decoder may process this.
        await using var bytes = new MemoryStream([0x49, 0x49, 0x2b, 0, 8, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255]);
        var image = new FormFile(bytes, 0, bytes.Length, "image", "photo" + extension) { Headers = new HeaderDictionary(), ContentType = mime };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(post.Id, image, "photo"));
        await app.WithDatabase(async db => Assert.Empty(await db.MediaAssets.ToArrayAsync()));
    }

    private sealed class ActivationFault { public bool Fail { get; set; } }
    private sealed class FailingActivation(GenerationActivation inner, ActivationFault fault) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fault.Fail ? throw new IOException("Injected pointer replacement failure") : inner.ActivateAsync(manifest);
    }
    private sealed class SaveFault : SaveChangesInterceptor
    {
        public bool AfterActivation { get; set; }
        public bool Media { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (AfterActivation && eventData.Context!.ChangeTracker.Entries<SiteGeneration>().Any(x => x.Entity.Status == GenerationStatus.Succeeded && x.State == EntityState.Modified) ||
                Media && eventData.Context!.ChangeTracker.Entries<MediaAsset>().Any(x => x.State == EntityState.Added))
                throw new IOException("Injected database commit failure");
            return ValueTask.FromResult(result);
        }
    }
}
