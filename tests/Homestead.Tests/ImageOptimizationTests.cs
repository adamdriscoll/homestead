using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Homestead.Tests;

public sealed class ImageOptimizationTests
{
    [Theory]
    [MemberData(nameof(ImageUploadValidationTests.SupportedImages), MemberType = typeof(ImageUploadValidationTests))]
    public void Independent_formats_can_be_optimized(string name, string extension, string mime, int width, int height)
    {
        var result = ImageOptimizer.Optimize(TestImages.Fixture(name), extension, mime);
        Assert.Equal((width, height, result.MimeType), ImageUploadValidator.Validate(result.Bytes, result.Extension, result.MimeType));
        Assert.Equal(1, ImageOptimizer.ReadOrientation(result.Bytes, result.Extension));
    }

    [Fact]
    public void Large_photos_are_resized_compressed_and_small_transparent_images_are_not_upscaled()
    {
        using var bitmap = new SKBitmap(4000, 2000);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SkyBlue);
            using var paint = new SKPaint { Color = SKColors.ForestGreen };
            for (var i = 0; i < 4000; i += 11) canvas.DrawCircle(i, i % 2000, 17, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var result = ImageOptimizer.Optimize(jpeg.ToArray(), ".jpg", "image/jpeg");
        Assert.Equal((2560, 1280), (result.Width, result.Height));
        Assert.True(result.Bytes.Length < jpeg.Size / 2, $"{jpeg.Size} -> {result.Bytes.Length}");
        Assert.Equal((2560, 1280, result.MimeType), ImageUploadValidator.Validate(result.Bytes, result.Extension, result.MimeType));
        var small = ImageOptimizer.Optimize(TestImages.Create(12, 7), ".png", "image/png");
        Assert.Equal((12, 7), (small.Width, small.Height));
        using var decoded = SKBitmap.Decode(small.Bytes);
        Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
    }

    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(2, false, 1)]
    [InlineData(3, false, 3)]
    [InlineData(4, false, 2)]
    [InlineData(5, true, 0)]
    [InlineData(6, true, 2)]
    [InlineData(7, true, 3)]
    [InlineData(8, true, 1)]
    public void Orientation_is_applied_before_metadata_is_removed(int orientation, bool swapped, int topLeft)
    {
        using var bitmap = new SKBitmap(60, 40);
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
            for (var i = 0; i < 4; i++)
            {
                paint.Color = colors[i];
                canvas.DrawRect(i % 2 * 30, i / 2 * 20, 30, 20, paint);
            }
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = WithOrientation(png.ToArray(), orientation);
        var result = ImageOptimizer.Optimize(bytes, ".png", "image/png");
        Assert.Equal(swapped ? (40, 60) : (60, 40), (result.Width, result.Height));
        Assert.Equal(1, ImageOptimizer.ReadOrientation(result.Bytes, result.Extension));
        using var decoded = SKBitmap.Decode(result.Bytes);
        var actual = decoded.GetPixel(5, 5); var expected = colors[topLeft];
        Assert.InRange(Math.Abs(actual.Red - expected.Red), 0, 30);
        Assert.InRange(Math.Abs(actual.Green - expected.Green), 0, 30);
        Assert.InRange(Math.Abs(actual.Blue - expected.Blue), 0, 30);
    }

    [Theory]
    [InlineData("porch")]
    [InlineData("journal")]
    [InlineData("ledger")]
    [InlineData("folio")]
    [InlineData("townsquare")]
    public async Task Optimized_photos_work_in_every_theme_and_originals_remain_private(string theme)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client(); using var anonymous = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        await app.WithDatabase(async db => { (await db.SiteSettings.SingleAsync()).ThemeId = theme; await db.SaveChangesAsync(); });
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        var post = await articles.CreateAsync(PostType.Gallery);
        var original = TestImages.Fixture("metadata.jpg");
        using var stream = new MemoryStream(original);
        var photo = await media.UploadAsync(post.Id, new FormFile(stream, 0, original.Length, "Upload", "photo.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" }, "Garden", retainOriginal: true);
        var input = ArticleInput.From((await articles.FindAsync(post.Id))!); input.Title = "Optimized gallery"; input.Tags = "Home";
        await articles.SaveAsync(post.Id, input);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(photo.PrivateUrl + "?original=true")).StatusCode);
        using var download = await author.GetAsync(photo.PrivateUrl + "?original=true");
        Assert.Equal(original, await download.Content.ReadAsByteArrayAsync()); Assert.Equal("no-store", download.Headers.CacheControl!.ToString());
        var stored = await author.GetByteArrayAsync(photo.PrivateUrl);
        Assert.NotEqual(original, stored);
        Assert.Contains(photo.PrivateUrl, await author.GetStringAsync($"/admin/posts/{post.Id}/preview"));
        Assert.True((await scope.ServiceProvider.GetRequiredService<PublishingService>().TransitionAsync(post.Id, "publish", 1)).Succeeded);
        foreach (var url in new[] { "/", "/galleries/", "/tags/home/", "/posts/optimized-gallery/" })
            Assert.Contains(photo.Url, await anonymous.GetStringAsync(url));
        Assert.Equal(stored, await anonymous.GetByteArrayAsync(photo.Url));
        string retained = "";
        await app.WithDatabase(async db => retained = (await db.MediaAssets.SingleAsync()).RetainedOriginalFilename!);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/media/{photo.Id:D}/{retained}")).StatusCode);
        await using var archive = await scope.ServiceProvider.GetRequiredService<StaticExportService>().CreateAsync();
        using var zip = new ZipArchive(archive);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains(retained));
        Assert.NotNull(zip.GetEntry(photo.Url.TrimStart('/')));
    }

    [Fact]
    public async Task Batch_accepts_50_rejects_51_and_defaults_to_discarding_originals()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author); _ = await TestApplication.Login(author);
        await using var scope = app.Services.CreateAsyncScope();
        var post = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync(PostType.Gallery);
        var path = $"/admin/posts/{post.Id}/edit";
        var html = await author.GetStringAsync(path);
        Assert.DoesNotContain("name=\"UploadAlt\"", html);
        Assert.Contains("Retain original images privately", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        foreach (var count in new[] { 51, 50 })
        {
            using var form = new MultipartFormDataContent(); form.Add(new StringContent(token), "__RequestVerificationToken");
            for (var i = 0; i < count; i++)
            {
                var content = new ByteArrayContent(TestImages.Create()); content.Headers.ContentType = new("image/png");
                form.Add(content, "Uploads", $"photo-{i}.png");
            }
            Assert.Equal(count == 50 ? HttpStatusCode.Redirect : HttpStatusCode.BadRequest, (await author.PostAsync(path + "?handler=Upload", form)).StatusCode);
            await app.WithDatabase(async db => Assert.Equal(count == 50 ? 50 : 0, await db.MediaAssets.CountAsync()));
        }
        await app.WithDatabase(async db =>
        {
            var assets = await db.MediaAssets.ToArrayAsync();
            Assert.All(assets, asset => { Assert.Null(asset.RetainedOriginalFilename); Assert.Null(asset.RetainedOriginalSize); });
            Assert.Equal(Enumerable.Range(0, 50), await db.PostMedia.OrderBy(x => x.SortOrder).Select(x => x.SortOrder).ToArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"/admin/media/{assets[0].Id}?original=true")).StatusCode);
        });
        Assert.Equal(50, Directory.GetFiles(Path.Combine(data.Path, "media", "originals"), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Backups_restore_retained_originals_and_hugo_excludes_them()
    {
        using var data = new TemporaryData(); using var restored = new TemporaryData(); using var files = new TemporaryData();
        using var app = new TestApplication(data.Path); using var author = app.Client();
        _ = await TestApplication.Setup(author);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>(); var post = await articles.CreateAsync(PostType.Gallery);
        var original = TestImages.Fixture("metadata.png"); using var stream = new MemoryStream(original);
        var photo = await scope.ServiceProvider.GetRequiredService<MediaService>().UploadAsync(post.Id,
            new FormFile(stream, 0, original.Length, "Upload", "photo.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }, "", retainOriginal: true);
        string retained = ""; await app.WithDatabase(async db => retained = (await db.MediaAssets.SingleAsync()).RetainedOriginalFilename!);
        var relative = $"media/originals/{photo.Id:D}/{retained}";
        Directory.CreateDirectory(files.Path); var backupPath = Path.Combine(files.Path, "backup.zip");
        await using (var archive = await scope.ServiceProvider.GetRequiredService<BackupService>().CreateAsync())
        await using (var output = File.Create(backupPath)) await archive.CopyToAsync(output);
        await BackupService.RestoreAsync(backupPath, restored.Path);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(restored.Path, relative)));
        Assert.True(File.Exists(Path.Combine(restored.Path, "media", "originals", photo.Id.ToString("D"), photo.Filename)));
        await using var hugo = await scope.ServiceProvider.GetRequiredService<HugoExportService>().CreateAsync();
        using var zip = new ZipArchive(hugo); Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains(retained));
        var invalidPath = Path.Combine(files.Path, "missing-original.zip");
        var manifestRelative = relative;
        using (var invalid = ZipFile.Open(invalidPath, ZipArchiveMode.Create))
        using (var valid = ZipFile.OpenRead(backupPath))
        {
            foreach (var entry in valid.Entries.Where(entry => entry.FullName != manifestRelative))
            {
                using var input = entry.Open(); using var output = invalid.CreateEntry(entry.FullName).Open(); input.CopyTo(output);
            }
        }
        using var rejected = new TemporaryData();
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupService.RestoreAsync(invalidPath, rejected.Path));
    }

    private static byte[] WithOrientation(byte[] png, int orientation)
    {
        var tiff = new byte[] { 73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0 };
        using var output = new MemoryStream(); output.Write(png.AsSpan(0, 33));
        var length = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)tiff.Length); output.Write(length);
        output.Write("eXIf"u8); output.Write(tiff); output.Write(new byte[4]); // Optional metadata is stripped before native decoding.
        output.Write(png.AsSpan(33)); return output.ToArray();
    }
}
