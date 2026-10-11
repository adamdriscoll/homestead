using System.Buffers.Binary;
using System.IO.Compression;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homestead.Tests;

public sealed class ImageUploadValidationTests
{
    public static TheoryData<string, string, string, int, int> SupportedImages => new()
    {
        { "PNG.png", ".png", "image/png", 3, 4 },
        { "interlaced-PNG.png", ".png", "image/png", 16, 16 },
        { "palette-PNG.png", ".png", "image/png", 3, 4 },
        { "16bit-PNG.png", ".png", "image/png", 3, 4 },
        { "animated-PNG.png", ".png", "image/png", 3, 4 },
        { "JPEG.jpg", ".jpg", "image/jpeg", 3, 4 },
        { "noninterleaved-JPEG.jpg", ".jpg", "image/jpeg", 3, 4 },
        { "Pillow-progressive.jpg", ".jpg", "image/jpeg", 3, 4 },
        { "Pillow-CMYK.jpg", ".jpg", "image/jpeg", 3, 4 },
        { "lossy-WebP.webp", ".webp", "image/webp", 3, 4 },
        { "lossless-WebP.webp", ".webp", "image/webp", 3, 4 },
        { "animated-WebP.webp", ".webp", "image/webp", 3, 4 },
        { "metadata.png", ".png", "image/png", 3, 4 },
        { "metadata.jpg", ".jpg", "image/jpeg", 3, 4 },
        { "metadata.webp", ".webp", "image/webp", 3, 4 }
    };

    [Theory]
    [MemberData(nameof(SupportedImages))]
    public void Independent_images_decode_and_every_truncated_prefix_is_rejected(string name, string extension, string mime, int width, int height)
    {
        var bytes = TestImages.Fixture(name);
        var original = bytes.ToArray();
        var result = ImageUploadValidator.Validate(bytes, extension, mime);
        Assert.Equal((width, height, mime), result);
        Assert.Equal(original, bytes);
        for (var length = 1; length < bytes.Length; length++)
            Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(bytes[..length], extension, mime));
    }

    [Theory]
    [InlineData("metadata.png", "image/png")]
    [InlineData("metadata.jpg", "image/jpeg")]
    [InlineData("metadata.webp", "image/webp")]
    public void Malformed_metadata_is_excluded_before_native_codec_creation(string name, string mime)
    {
        var bytes = TestImages.Fixture(name);
        Assert.True(bytes.AsSpan().IndexOf("hostile-metadata"u8) >= 0);
        var filtered = ImageUploadValidator.CreateDecodeInput(bytes, mime);
        Assert.True(filtered.Length < bytes.Length);
        Assert.True(filtered.AsSpan().IndexOf("hostile-metadata"u8) < 0);
        if (mime == "image/png")
            foreach (var type in new[] { "iCCP", "eXIf", "zTXt", "iTXt" }) Assert.True(filtered.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(type)) < 0);
        if (mime == "image/webp") Assert.Equal(0, filtered[20] & 0x2c);
        Assert.Equal((3, 4, mime), ImageUploadValidator.Validate(bytes, Path.GetExtension(name), mime));
    }

    [Fact]
    public void Extension_and_mime_must_match_before_container_parsing_and_empty_mime_remains_supported()
    {
        var signatureOnly = TestImages.Fixture("PNG.png")[..8];
        foreach (var (extension, mime) in new[] { (".jpg", "image/jpeg"), (".png", "image/webp") })
            Assert.Contains("must match", Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(signatureOnly, extension, mime)).Message);
        foreach (var mime in new[] { "", null })
            Assert.Equal((3, 4, "image/png"), ImageUploadValidator.Validate(TestImages.Fixture("PNG.png"), ".png", mime));
        Assert.Equal((3, 4, "image/jpeg"), ImageUploadValidator.Validate(TestImages.Fixture("JPEG.jpg"), ".jpeg", "image/jpeg"));
        Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate("II*\0fake TIFF"u8.ToArray(), ".png", "image/png"));
    }

    [Fact]
    public void Pixel_limit_checks_do_not_overflow_or_allocate_pixels_from_oversized_headers()
    {
        var png = TestImages.Fixture("PNG.png");
        foreach (var (width, height) in new[] { (uint.MaxValue, uint.MaxValue), (8001u, 5000u) })
        {
            var oversized = png.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16), width);
            BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(20), height);
            Assert.Contains("40 megapixels", Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(oversized, ".png", "image/png")).Message);
        }
        var jpeg = TestImages.Fixture("JPEG.jpg");
        var sof = FindJpegSof(jpeg);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 5), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + 7), ushort.MaxValue);
        Assert.Contains("40 megapixels", Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(jpeg, ".jpg", "image/jpeg")).Message);
        var webp = TestImages.Fixture("metadata.webp");
        webp.AsSpan(24, 6).Fill(255);
        Assert.Contains("40 megapixels", Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(webp, ".webp", "image/webp")).Message);
    }

    [Fact]
    public void Exactly_forty_million_pixels_are_accepted()
    {
        var png = SolidPng(8000, 5000);
        Assert.Equal((8000, 5000, "image/png"), ImageUploadValidator.Validate(png, ".png", "image/png"));
    }

    [Fact]
    public void Exactly_twenty_mebibytes_are_accepted_and_one_more_byte_is_rejected()
    {
        var png = TestImages.Fixture("PNG.png");
        using var output = new MemoryStream();
        output.Write(png.AsSpan(0, 33));
        WritePngChunk(output, "tEXt", new byte[ImageUploadValidator.MaximumBytes - png.Length - 12]);
        output.Write(png.AsSpan(33));
        var bytes = output.ToArray();
        Assert.Equal(ImageUploadValidator.MaximumBytes, bytes.Length);
        Assert.Equal((3, 4, "image/png"), ImageUploadValidator.Validate(bytes, ".png", "image/png"));
        Array.Resize(ref bytes, bytes.Length + 1);
        Assert.Contains("20 MB", Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(bytes, ".png", "image/png")).Message);
        Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate([], ".png", "image/png"));
    }

    [Fact]
    public void Damaged_checksums_payloads_container_lengths_and_interlaced_data_are_rejected()
    {
        var png = TestImages.Fixture("PNG.png");
        var badCrc = png.ToArray(); badCrc[29] ^= 1;
        var badLength = png.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(8), uint.MaxValue);
        var badPixels = SolidPng(3, 4, invalidPixels: true);
        foreach (var bytes in new[] { badCrc, badLength, badPixels, TestImages.Fixture("invalid-small-interlaced-PNG.png") })
            Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(bytes, ".png", "image/png"));
        var webp = TestImages.Fixture("lossless-WebP.webp");
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(webp, ".webp", "image/webp"));
        var jpeg = TestImages.Fixture("JPEG.jpg");
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4), ushort.MaxValue);
        Assert.Throws<InvalidOperationException>(() => ImageUploadValidator.Validate(jpeg, ".jpg", "image/jpeg"));
    }

    [Theory]
    [InlineData("animated-PNG.png", "image/png")]
    [InlineData("animated-WebP.webp", "image/webp")]
    public void Animation_validation_decodes_only_the_first_frame(string name, string mime)
    {
        var bytes = TestImages.Fixture(name);
        if (mime == "image/png")
        {
            using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, 8));
            for (var position = 8; position < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position));
                var chunk = bytes.AsSpan(position, length + 12);
                if (chunk.Slice(4, 4).SequenceEqual("fdAT"u8))
                {
                    var payload = chunk.Slice(8, length).ToArray();
                    payload.AsSpan(4).Fill(255); // Invalid zlib data in frame two; preserve its sequence and CRC.
                    WritePngChunk(output, "fdAT", payload);
                }
                else output.Write(chunk);
                position += chunk.Length;
            }
            bytes = output.ToArray();
        }
        else
        {
            var frames = 0;
            for (var position = 12; position < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 4));
                if (bytes.AsSpan(position, 4).SequenceEqual("ANMF"u8) && ++frames == 2)
                    bytes.AsSpan(position + 37, length - 29).Fill(255); // Retain VP8L header; damage only frame-two entropy.
                position += 8 + length + (length & 1);
            }
            Assert.Equal(2, frames);
        }
        Assert.Equal((3, 4, mime), ImageUploadValidator.Validate(bytes, Path.GetExtension(name), mime));
        using var data = SkiaSharp.SKData.CreateCopy(ImageUploadValidator.CreateDecodeInput(bytes, mime));
        using var codec = SkiaSharp.SKCodec.Create(data);
        using var bitmap = new SkiaSharp.SKBitmap(codec.Info);
        Assert.NotEqual(SkiaSharp.SKCodecResult.Success, codec.GetPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, new SkiaSharp.SKCodecOptions(1)));
    }

    [Theory]
    [InlineData("metadata.png", "image/png")]
    [InlineData("metadata.jpg", "image/jpeg")]
    [InlineData("metadata.webp", "image/webp")]
    public async Task Post_and_branding_uploads_store_the_exact_original_bytes(string name, string mime)
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        var bytes = TestImages.Fixture(name);
        var post = await articles.CreateAsync();
        using var input = new MemoryStream(bytes);
        var file = new FormFile(input, 0, bytes.Length, "Upload", name) { Headers = new HeaderDictionary(), ContentType = mime };
        var photo = await media.UploadAsync(post.Id, file, "Original photo");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(media.OriginalPath(photo)));
        input.Position = 0;
        var branding = await media.UploadBrandingAsync("Favicon", file);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(media.OriginalPath(branding)));
        await app.WithDatabase(async db =>
        {
            Assert.Equal(2, await db.MediaAssets.CountAsync());
            foreach (var asset in await db.MediaAssets.ToArrayAsync())
            { Assert.Equal(3, asset.Width); Assert.Equal(4, asset.Height); Assert.Equal(mime, asset.MimeType); Assert.Equal(bytes.Length, asset.Size); }
        });
    }

    [Fact]
    public async Task Failed_validation_and_misreported_stream_length_create_no_records_or_originals()
    {
        using var data = new TemporaryData(); using var app = new TestApplication(data.Path); using var client = app.Client();
        _ = await TestApplication.Setup(client);
        await using var scope = app.Services.CreateAsyncScope();
        var post = await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        foreach (var slot in new[] { "post", "Favicon" })
        {
            using var input = new MemoryStream(TestImages.Fixture("PNG.png")[..32]);
            var file = new FormFile(input, 0, input.Length, "Upload", "broken.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
            await Assert.ThrowsAsync<InvalidOperationException>(() => slot == "post" ? media.UploadAsync(post.Id, file, "") : media.UploadBrandingAsync(slot, file));
        }
        var oversized = new MisreportedFile(new byte[ImageUploadValidator.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => media.UploadAsync(post.Id, oversized, ""));
        await app.WithDatabase(async db => { Assert.Empty(await db.MediaAssets.ToListAsync()); Assert.Null((await db.SiteSettings.SingleAsync()).FaviconMediaId); });
        Assert.Empty(Directory.GetFiles(Path.Combine(data.Path, "media", "originals"), "*", SearchOption.AllDirectories));
    }

    private static int FindJpegSof(byte[] bytes)
    {
        for (var position = 2; position < bytes.Length;)
        {
            if (bytes[position + 1] is >= 192 and <= 195) return position;
            position += 2 + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 2));
        }
        throw new InvalidOperationException("The JPEG fixture needs a frame header.");
    }

    private static byte[] SolidPng(int width, int height, bool invalidPixels = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        WritePngChunk(output, "IHDR", header);
        using var pixels = new MemoryStream();
        using (var compressed = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 3 + 1];
            if (invalidPixels) row[0] = 255;
            for (var y = 0; y < height; y++) compressed.Write(row);
        }
        WritePngChunk(output, "IDAT", pixels.ToArray()); WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] payload)
    {
        var chunk = new byte[payload.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4); payload.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, payload.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : crc >> 1 ^ 0xedb88320;
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(payload.Length + 8), ~crc);
        output.Write(chunk);
    }

    private sealed class MisreportedFile(byte[] bytes) : IFormFile
    {
        public string ContentType => "image/png";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => 1;
        public string Name => "Upload";
        public string FileName => "photo.png";
        public Stream OpenReadStream() => new MemoryStream(bytes);
        public void CopyTo(Stream target) => target.Write(bytes);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => target.WriteAsync(bytes, cancellationToken).AsTask();
    }
}
