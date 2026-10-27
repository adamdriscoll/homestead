using System.Buffers.Binary;
using SkiaSharp;

namespace Homestead.Features.Media;

// Isolate optional metadata from the native decoder.
public static class ImageUploadValidator
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    private const long MaximumPixels = 40_000_000;
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static (int Width, int Height, string MimeType) Validate(byte[] original, string extension, string? contentType)
    {
        using var bitmap = Decode(original, extension, contentType);
        return (bitmap.Width, bitmap.Height, Detect(original));
    }

    internal static SKBitmap Decode(byte[] original, string extension, string? contentType)
    {
        if (original.Length is <= 0 or > MaximumBytes)
            throw new InvalidOperationException("Images must be between 1 byte and 20 MB.");
        var mime = Detect(original);
        var expected = extension switch { ".png" => "image/png", ".webp" => "image/webp", ".jpg" or ".jpeg" => "image/jpeg", _ => "" };
        if (mime != expected || (!string.IsNullOrEmpty(contentType) && contentType != mime))
            throw new InvalidOperationException("The file contents, extension, and image type must match.");

        var input = CreateDecodeInput(original, mime);
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw Damaged();
        var format = mime switch { "image/png" => SKEncodedImageFormat.Png, "image/webp" => SKEncodedImageFormat.Webp, _ => SKEncodedImageFormat.Jpeg };
        if (codec.EncodedFormat != format) throw Damaged();
        var info = codec.Info;
        CheckDimensions(info.Width, info.Height);
        // Use a predictable four-byte pixel format. Info is checked before allocation.
        var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            if (bitmap.GetPixels() == IntPtr.Zero) throw Damaged();
            var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(0));
            if (result != SKCodecResult.Success) throw Damaged();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static string Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw Damaged();
    }

    internal static byte[] CreateDecodeInput(ReadOnlySpan<byte> original, string mime)
    {
        using var output = new MemoryStream(original.Length);
        switch (mime)
        {
            case "image/jpeg": FilterJpeg(original, output); break;
            case "image/png": FilterPng(original, output); break;
            case "image/webp": FilterWebp(original, output); break;
            default: throw Damaged();
        }
        return output.ToArray();
    }

    private static void FilterJpeg(ReadOnlySpan<byte> bytes, MemoryStream output)
    {
        output.Write(bytes[..2]);
        var position = 2;
        while (position < bytes.Length)
        {
            var start = position;
            if (bytes[position++] != 255) throw Damaged();
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position == bytes.Length) throw Damaged();
            var marker = bytes[position++];
            if (marker == 217)
            {
                output.Write(bytes[start..position]);
                return;
            }
            if (marker is 0 or 216 or >= 208 and <= 215) throw Damaged();
            if (marker == 1) { output.Write(bytes[start..position]); continue; }
            if (bytes.Length - position < 2) throw Damaged();
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (size < 2 || size > bytes.Length - position) throw Damaged();
            var payload = bytes.Slice(position + 2, size - 2);
            if (marker is >= 192 and <= 207 and not (196 or 200 or 204))
            {
                if (payload.Length < 6) throw Damaged();
                CheckDimensions(BinaryPrimitives.ReadUInt16BigEndian(payload[3..]), BinaryPrimitives.ReadUInt16BigEndian(payload[1..]));
            }
            position += size;
            // APP14's Adobe color-transform flag is needed for CMYK/YCCK decoding.
            // No ICC/EXIF/XMP/JFIF thumbnails/comments reach Skia.
            var adobe = marker == 238 && payload.Length == 12 && payload[..5].SequenceEqual("Adobe"u8);
            if (marker is not (>= 224 and <= 239) && marker != 254 || adobe)
                output.Write(bytes[start..position]);
            if (marker != 218) continue;

            // Preserve entropy bytes verbatim, including stuffed FFs, restart markers,
            // and successive progressive scans. Require the original EOI marker.
            start = position;
            while (position < bytes.Length)
            {
                if (bytes[position] != 255) { position++; continue; }
                var next = position + 1;
                while (next < bytes.Length && bytes[next] == 255) next++;
                if (next == bytes.Length) throw Damaged();
                if (bytes[next] is 0 or >= 208 and <= 215) { position = next + 1; continue; }
                break;
            }
            output.Write(bytes[start..position]);
        }
        throw Damaged();
    }

    private static void FilterPng(ReadOnlySpan<byte> bytes, MemoryStream output)
    {
        output.Write(bytes[..8]);
        var position = 8;
        var header = false;
        var pixels = false;
        var pixelsEnded = false;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < 12) throw Damaged();
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            if (size > bytes.Length - position - 12) throw Damaged();
            var length = (int)size;
            var chunk = bytes.Slice(position, length + 12);
            var type = chunk.Slice(4, 4);
            if (!header)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) throw Damaged();
                CheckDimensions(BinaryPrimitives.ReadUInt32BigEndian(chunk[8..]), BinaryPrimitives.ReadUInt32BigEndian(chunk[12..]));
                header = true;
            }
            else if (type.SequenceEqual("IHDR"u8)) throw Damaged();
            if (type.SequenceEqual("IDAT"u8))
            {
                if (pixelsEnded) throw Damaged();
                pixels = true;
            }
            else if (pixels) pixelsEnded = true;
            // Retain pixel, palette, transparency, and animation structure only.
            var keep = (type[0] & 32) == 0 || type.SequenceEqual("tRNS"u8) || type.SequenceEqual("acTL"u8) || type.SequenceEqual("fcTL"u8) || type.SequenceEqual("fdAT"u8);
            if (keep)
            {
                if (Crc(chunk.Slice(4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(chunk[(length + 8)..])) throw Damaged();
                output.Write(chunk);
            }
            position += length + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !pixels) throw Damaged();
                return;
            }
        }
        throw Damaged();
    }

    private static void FilterWebp(ReadOnlySpan<byte> bytes, MemoryStream output)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (size < 4 || size > bytes.Length - 8) throw Damaged();
        output.Write(bytes[..12]);
        FilterWebpChunks(bytes.Slice(12, (int)size - 4), output, false);
        var buffer = output.GetBuffer();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)output.Length - 8);
    }

    private static void FilterWebpChunks(ReadOnlySpan<byte> bytes, MemoryStream output, bool frame)
    {
        var position = 0;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < 8) throw Damaged();
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
            var padded = (long)size + (size & 1);
            if (padded > bytes.Length - position - 8) throw Damaged();
            var chunk = bytes.Slice(position, 8 + (int)padded);
            var type = chunk[..4];
            var payload = chunk.Slice(8, (int)size);
            if (!frame && type.SequenceEqual("VP8X"u8))
            {
                if (size != 10) throw Damaged();
                CheckDimensions(ReadUInt24(payload[4..]) + 1, ReadUInt24(payload[7..]) + 1);
                var start = (int)output.Position;
                output.Write(chunk);
                output.GetBuffer()[start + 8] &= 0xd3; // Clear ICCP, EXIF, XMP flags only.
            }
            else if (!frame && type.SequenceEqual("ANMF"u8))
            {
                if (size < 16) throw Damaged();
                CheckDimensions(ReadUInt24(payload[6..]) + 1, ReadUInt24(payload[9..]) + 1);
                var start = (int)output.Position;
                output.Write(chunk[..24]);
                FilterWebpChunks(payload[16..], output, true);
                var filteredSize = (uint)(output.Position - start - 8);
                BinaryPrimitives.WriteUInt32LittleEndian(output.GetBuffer().AsSpan(start + 4), filteredSize);
                if ((filteredSize & 1) != 0) output.WriteByte(0);
            }
            else if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8) || type.SequenceEqual("ALPH"u8) || !frame && type.SequenceEqual("ANIM"u8))
                output.Write(chunk);
            position += chunk.Length;
        }
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);

    private static void CheckDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0) throw Damaged();
        if (width > MaximumPixels / height) throw new InvalidOperationException("Choose an image of at most 40 megapixels.");
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[i] = value;
        }
        return table;
    }

    private static InvalidOperationException Damaged() => new("This image is damaged or unsupported. Export it as JPEG, PNG, or WebP.");
}
