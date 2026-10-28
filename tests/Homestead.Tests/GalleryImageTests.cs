using System.IO.Compression;
using System.Net;
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

public sealed class GalleryImageTests
{
    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Gallery_images_have_static_pages_and_private_previews_in_every_theme(string theme)
    {
        using var data = new TemporaryData(); var failActivation = false;
        using var app = new TestApplication(data.Path, configureServices: services =>
            services.AddSingleton<IGenerationActivation>(provider => new FailingActivation(
                new GenerationActivation(provider.GetRequiredService<StoragePaths>(), provider.GetRequiredService<ActiveSite>()), () => failActivation)));
        using var owner = app.Client(); using var visitor = app.Client();
        _ = await TestApplication.Setup(owner); _ = await TestApplication.Login(owner);
        await app.WithDatabase(async db =>
        {
            var settings = await db.SiteSettings.SingleAsync(); settings.ThemeId = theme;
            await db.SaveChangesAsync();
        });
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var publishing = scope.ServiceProvider.GetRequiredService<PublishingService>();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        var post = await articles.CreateAsync(PostType.Gallery);
        var images = new List<ArticleMedia>();
        for (var i = 0; i < 3; i++)
        {
            using var bytes = new MemoryStream(TestImages.Create());
            images.Add(await media.UploadAsync(post.Id,
                new FormFile(bytes, 0, bytes.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, $"Photo {i + 1}"));
        }
        var input = ArticleInput.From((await articles.FindAsync(post.Id))!);
        input.Title = "Garden gallery"; input.Tags = "Garden";
        input.BodyMarkdown = $"Inline photo: ![First]({images[0].Url})";
        input.Captions = images.ToDictionary(x => x.Id, _ => "Sky & <clouds>");
        await articles.SaveAsync(post.Id, input);
        var previewPath = $"/admin/posts/{post.Id}/preview?image={images[1].Id}";
        var preview = await owner.GetAsync(previewPath);
        Assert.Equal("no-store", preview.Headers.CacheControl!.ToString());
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Assert.Contains(images[1].PrivateUrl, previewHtml); Assert.Contains("noindex, nofollow", previewHtml);
        Assert.Contains("data-gallery-image", previewHtml); Assert.DoesNotContain("<script", previewHtml);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync(previewPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/admin/posts/{post.Id}/preview?image={Guid.NewGuid()}")).StatusCode);
        var snapshot = ArticleService.Snapshot((await articles.FindAsync(post.Id))!, "garden-gallery", DateTime.UtcNow);
        foreach (var image in images)
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(GalleryImages.Path(snapshot, image))).StatusCode);
        Assert.True((await publishing.TransitionAsync(post.Id, "publish", input.Version + 1)).Succeeded);
        var active = app.Services.GetRequiredService<ActiveSite>().Current!;
        snapshot = Assert.Single(active.Articles);
        var paths = images.Select(image => GalleryImages.Path(snapshot, image)).ToArray();
        foreach (var listing in new[] { "/", "/galleries/", "/tags/garden/", "/posts/garden-gallery/" })
        {
            var html = await visitor.GetStringAsync(listing);
            Assert.Contains($"href=\"{paths[0]}\"", html);
            Assert.DoesNotContain("<script", html);
        }
        for (var i = 0; i < images.Count; i++)
        {
            var html = await visitor.GetStringAsync(paths[i]);
            Assert.Contains("data-gallery-image", html); Assert.Contains($"data-public-theme=\"{theme}\"", html);
            Assert.Contains($"href=\"https://example.test{paths[i]}\"", html);
            Assert.Contains($"property=\"og:image\" content=\"https://example.test{images[i].Url}\"", html);
            Assert.Contains("Sky &amp; &lt;clouds&gt;", html); Assert.Contains("Back to gallery", html);
            Assert.Equal(i > 0, html.Contains("Previous photo", StringComparison.Ordinal));
            Assert.Equal(i < 2, html.Contains("Next photo", StringComparison.Ordinal));
            Assert.DoesNotContain("<script", html); Assert.DoesNotContain("/admin/media/", html);
            Assert.Contains(paths[i].TrimStart('/') + "index.html", active.Files);
            Assert.Contains("https://example.test" + paths[i], await visitor.GetStringAsync("/sitemap.xml"));
        }
        await using (var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync())
        using (var zip = new ZipArchive(archive))
            foreach (var path in paths) Assert.NotNull(zip.GetEntry(path.TrimStart('/') + "index.html"));

        input = ArticleInput.From((await articles.FindAsync(post.Id))!);
        input.Captions[images[1].Id] = "PRIVATE CAPTION";
        await articles.SaveAsync(post.Id, input);
        Assert.Contains("PRIVATE CAPTION", await owner.GetStringAsync(previewPath));
        Assert.DoesNotContain("PRIVATE CAPTION", await visitor.GetStringAsync(paths[1]));
        // A failed desired removal must leave the complete active gallery, including photo pages.
        failActivation = true;
        Assert.False((await publishing.TransitionAsync(post.Id, "unpublish", input.Version + 1)).Succeeded);
        foreach (var path in paths) Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(path)).StatusCode);
        failActivation = false;
        Assert.True((await publishing.RebuildAsync()).Succeeded);
        foreach (var path in paths) Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync(path)).StatusCode);
    }

    private sealed class FailingActivation(IGenerationActivation inner, Func<bool> fail) : IGenerationActivation
    {
        public Task ActivateAsync(GenerationManifest manifest) => fail()
            ? throw new IOException("Injected image page activation failure.") : inner.ActivateAsync(manifest);
    }

    [Fact]
    public async Task New_sites_default_to_Town_Square_without_color_support_badges()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var owner = app.Client();
        _ = await TestApplication.Setup(owner); _ = await TestApplication.Login(owner);
        Assert.Equal("townsquare", app.Services.GetRequiredService<ActiveSite>().Current!.Settings.ThemeId);
        Assert.Contains("data-public-theme=\"townsquare\"", await owner.GetStringAsync("/"));
        Assert.DoesNotContain("colors supported", await owner.GetStringAsync("/admin/settings"));
    }
}
