using Homestead.Features.Media;
using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

// Throwaway compatibility experiment. No application, database, or persistent data.
// Run: dotnet run --project tests/prototypes/SkiaSharpUpload -c Release
var failures = 0;
foreach (var (name, encoder, extension, mime) in new (string, IImageEncoder, string, string)[]
{
    ("PNG", new PngEncoder(), ".png", "image/png"),
    ("JPEG", new JpegEncoder(), ".jpg", "image/jpeg"),
    ("noninterleaved JPEG", new JpegEncoder { Interleaved = false }, ".jpg", "image/jpeg"),
    ("interlaced PNG", new PngEncoder { InterlaceMethod = PngInterlaceMode.Adam7 }, ".png", "image/png"),
    ("invalid small interlaced PNG", new PngEncoder { InterlaceMethod = PngInterlaceMode.Adam7 }, ".png", "image/png"),
    ("palette PNG", new PngEncoder { ColorType = PngColorType.Palette }, ".png", "image/png"),
    ("16bit PNG", new PngEncoder { BitDepth = PngBitDepth.Bit16 }, ".png", "image/png"),
    ("animated PNG", new PngEncoder(), ".png", "image/png"),
    ("animated WebP", new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }, ".webp", "image/webp"),
    ("lossy WebP", new WebpEncoder { FileFormat = WebpFileFormatType.Lossy }, ".webp", "image/webp"),
    ("lossless WebP", new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }, ".webp", "image/webp")
})
{
    var width = name == "interlaced PNG" ? 16 : 3;
    var height = name == "interlaced PNG" ? 16 : 4;
    using var image = new Image<Rgba32>(width, height, new Rgba32(50, 100, 150));
    if (name.StartsWith("animated"))
    {
        using var next = new Image<Rgba32>(3, 4, new Rgba32(150, 100, 50));
        image.Frames.AddFrame(next.Frames.RootFrame);
    }
    using var output = new MemoryStream();
    image.Save(output, encoder);
    var bytes = output.ToArray();
    if (args.Length == 1)
    {
        Directory.CreateDirectory(args[0]);
        File.WriteAllBytes(Path.Combine(args[0], name.Replace(' ', '-') + extension), bytes);
    }
    (int Width, int Height, string MimeType) result;
    try { result = SkiaSharpUploadPrototype.Validate(bytes, extension, mime); }
    catch (InvalidOperationException ex)
    {
        using var raw = SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(bytes));
        using var bitmap = new SkiaSharp.SKBitmap(raw.Info);
        Console.WriteLine($"{name}: {(name.StartsWith("invalid") ? "EXPECTED REJECTION" : "FAILED")} {ex.Message}; unfiltered Skia result: {raw.GetPixels(bitmap.Info, bitmap.GetPixels())}");
        if (!name.StartsWith("invalid")) failures++;
        continue;
    }
    Console.WriteLine($"{name}: {result.Width} x {result.Height}; {result.MimeType}");
    if (result.Width != width || result.Height != height || name.StartsWith("invalid")) failures++;
    var acceptedTruncations = 0;
    var baselineTruncations = 0;
    for (var length = 1; length < bytes.Length; length++)
    {
        var cut = bytes[..length];
        try { SkiaSharpUploadPrototype.Validate(cut, extension, mime); acceptedTruncations++; } catch (InvalidOperationException) { }
        try { using var decoded = Image.Load(new DecoderOptions { MaxFrames = 1, SkipMetadata = true }, cut); baselineTruncations++; } catch (Exception) { }
    }
    Console.WriteLine($"  accepted truncated prefixes: Skia prototype {acceptedTruncations}, current ImageSharp {baselineTruncations}");
    if (acceptedTruncations != 0) failures++;
    if (args.Length == 1)
    {
        Directory.CreateDirectory(args[0]);
        File.WriteAllBytes(Path.Combine(args[0], name.Replace(' ', '-') + extension), bytes);
    }
}
if (args.Length == 1)
    foreach (var path in Directory.GetFiles(args[0], "Pillow-*.jpg"))
    {
        var result = SkiaSharpUploadPrototype.Validate(File.ReadAllBytes(path), ".jpg", "image/jpeg");
        Console.WriteLine($"{Path.GetFileName(path)}: {result.Width} x {result.Height}");
        if (result.Width != 3 || result.Height != 4) failures++;
    }
foreach (var extension in new[] { ".png", ".jpg", ".webp" })
{
    var mime = extension switch { ".png" => "image/png", ".jpg" => "image/jpeg", _ => "image/webp" };
    using var image = new Image<Rgba32>(3, 4);
    using var output = new MemoryStream();
    image.Save(output, extension == ".png" ? new PngEncoder() : extension == ".jpg" ? new JpegEncoder() : new WebpEncoder());
    var original = AddMalformedMetadata(output.ToArray(), extension);
    var result = SkiaSharpUploadPrototype.Validate(original, extension, mime);
    var filtered = SkiaSharpUploadPrototype.CreateDecodeInput(original, mime);
    var leaked = filtered.AsSpan().IndexOf("hostile-metadata"u8) >= 0;
    Console.WriteLine($"{mime} with malformed ICC/EXIF/text: {result.Width} x {result.Height}; metadata reached decoder: {leaked}");
    if (leaked) failures++;
    if (args.Length == 1) File.WriteAllBytes(Path.Combine(args[0], "metadata" + extension), original);
}
Console.WriteLine($"Verdict: {(failures == 0 ? "PASS" : "FAIL")}; {failures} requirement failures.");
return failures == 0 ? 0 : 1;

static byte[] AddMalformedMetadata(byte[] bytes, string extension)
{
    using var output = new MemoryStream();
    var payload = "hostile-metadata\0not-a-profile-or-zlib-stream"u8.ToArray();
    if (extension == ".png")
    {
        output.Write(bytes.AsSpan(0, 33));
        foreach (var type in new[] { "iCCP", "eXIf", "zTXt", "iTXt" })
        {
            var chunk = new byte[payload.Length + 12];
            BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            payload.CopyTo(chunk, 8);
            uint crc = uint.MaxValue;
            foreach (var value in chunk.AsSpan(4, payload.Length + 4))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : crc >> 1 ^ 0xedb88320;
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(payload.Length + 8), ~crc);
            output.Write(chunk);
        }
        output.Write(bytes.AsSpan(33));
    }
    else if (extension == ".jpg")
    {
        output.Write(bytes.AsSpan(0, 2));
        foreach (var (marker, prefix) in new[] { (225, "Exif\0\0"), (226, "ICC_PROFILE\0\x01\x01"), (254, "") })
        {
            var data = System.Text.Encoding.ASCII.GetBytes(prefix).Concat(payload).ToArray();
            output.Write(new byte[] { 255, (byte)marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2) });
            output.Write(data);
        }
        output.Write(bytes.AsSpan(2));
    }
    else
    {
        output.Write(bytes.AsSpan(0, 12));
        var hasExtended = bytes.AsSpan(12, 4).SequenceEqual("VP8X"u8);
        if (hasExtended)
        {
            output.Write(bytes.AsSpan(12, 18));
            output.GetBuffer()[20] |= 0x2c;
        }
        else
        {
            // A valid extended canvas with all three metadata flags set.
            output.Write("VP8X"u8); output.Write(new byte[] { 10, 0, 0, 0, 0x2c, 0, 0, 0, 2, 0, 0, 3, 0, 0 });
        }
        foreach (var type in new[] { "ICCP", "EXIF", "XMP " })
        {
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length); output.Write(size);
            output.Write(payload); if ((payload.Length & 1) != 0) output.WriteByte(0);
        }
        output.Write(bytes.AsSpan(hasExtended ? 30 : 12));
        BinaryPrimitives.WriteUInt32LittleEndian(output.GetBuffer().AsSpan(4), (uint)output.Length - 8);
    }
    return output.ToArray();
}
