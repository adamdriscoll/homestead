using System.Buffers.Binary;
using SkiaSharp;

namespace Homestead.Features.Media;

internal sealed record OptimizedImage(byte[] Bytes, string Extension, string MimeType, int Width, int Height);

internal static class ImageOptimizer
{
    public const int MaximumEdge = 2560;

    public static OptimizedImage Optimize(byte[] original, string extension, string? contentType)
    {
        using var bitmap = ImageUploadValidator.Decode(original, extension, contentType);
        var orientation = ReadOrientation(original, extension);
        var swapped = orientation is >= 5 and <= 8;
        var width = swapped ? bitmap.Height : bitmap.Width;
        var height = swapped ? bitmap.Width : bitmap.Height;
        var scale = Math.Min(1d, (double)MaximumEdge / Math.Max(width, height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)targetWidth / width, (float)targetHeight / height);
            // Map encoded pixels to their display orientation before discarding EXIF.
            var matrix = orientation switch
            {
                2 => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
                3 => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
                4 => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
                5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                6 => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
                7 => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
                8 => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
                _ => SKMatrix.Identity
            };
            canvas.Concat(matrix);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        using var result = SKImage.FromBitmap(target);
        using var webp = result.Encode(SKEncodedImageFormat.Webp, 82)
            ?? throw new InvalidOperationException("This image could not be optimized. Try exporting it again.");
        // A small PNG can be smaller than lossy WebP. Both candidates omit metadata
        // and retain transparency; never fall back to unfiltered original bytes.
        using var png = result.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("This image could not be optimized. Try exporting it again.");
        return png.Size < webp.Size
            ? new(png.ToArray(), ".png", "image/png", targetWidth, targetHeight)
            : new(webp.ToArray(), ".webp", "image/webp", targetWidth, targetHeight);
    }

    // Read only the fixed TIFF orientation field. Do not give optional metadata to
    // native codecs, follow nested IFDs, or allocate based on untrusted offsets.
    internal static int ReadOrientation(ReadOnlySpan<byte> bytes, string extension)
    {
        if (extension is ".jpg" or ".jpeg")
        {
            for (var offset = 2; offset + 4 <= bytes.Length;)
            {
                if (bytes[offset++] != 255) break;
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset >= bytes.Length) break;
                var marker = bytes[offset++];
                if (marker is 217 or 218) break;
                if (marker == 1) continue;
                if (offset + 2 > bytes.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (length < 2 || length > bytes.Length - offset) break;
                if (marker == 225 && bytes.Slice(offset + 2, length - 2).StartsWith("Exif\0\0"u8))
                    return TiffOrientation(bytes.Slice(offset + 8, length - 8));
                offset += length;
            }
        }
        else
        {
            var png = extension == ".png";
            for (var offset = png ? 8 : 12; offset + (png ? 12 : 8) <= bytes.Length;)
            {
                var length = png ? BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                var overhead = png ? 12 : 8;
                if (length > bytes.Length - offset - overhead) break;
                var type = bytes.Slice(offset + (png ? 4 : 0), 4);
                if (type.SequenceEqual(png ? "eXIf"u8 : "EXIF"u8))
                {
                    var payload = bytes.Slice(offset + 8, (int)length);
                    return TiffOrientation(payload.StartsWith("Exif\0\0"u8) ? payload[6..] : payload);
                }
                offset += overhead + (int)length + (png ? 0 : (int)(length & 1));
            }
        }
        return 1;
    }

    private static int TiffOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return 1;
        var little = tiff[..2].SequenceEqual("II"u8);
        if (!little && !tiff[..2].SequenceEqual("MM"u8) || U16(tiff[2..], little) != 42) return 1;
        var offset = U32(tiff[4..], little);
        if (offset < 8 || offset > tiff.Length - 2) return 1;
        var count = U16(tiff[(int)offset..], little);
        var entries = tiff[((int)offset + 2)..];
        if (count > entries.Length / 12) return 1;
        for (var i = 0; i < count; i++)
        {
            var entry = entries[(i * 12)..];
            if (U16(entry, little) != 0x112 || U16(entry[2..], little) != 3 || U32(entry[4..], little) != 1) continue;
            var value = U16(entry[8..], little);
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, bool little) => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint U32(ReadOnlySpan<byte> bytes, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
