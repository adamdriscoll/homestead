using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Homestead.Features.Media;

public sealed class MediaService(HomesteadDbContext db, ArticleService articles, PublicationLock writer, StoragePaths paths)
{
    public const long MaximumBytes = 20 * 1024 * 1024;
    public Task<ArticleMedia> UploadAsync(Guid postId, IFormFile file, string? altText, bool inlineOnly = false)
        => StoreAsync(postId, file, altText, null, inlineOnly);

    public Task<ArticleMedia> UploadBrandingAsync(string slot, IFormFile file)
    {
        if (slot is not ("Avatar" or "Logo" or "Header")) throw new InvalidOperationException("Choose an avatar, logo, or header image.");
        return StoreAsync(null, file, "", slot);
    }

    private async Task<ArticleMedia> StoreAsync(Guid? postId, IFormFile file, string? altText, string? slot, bool inlineOnly = false)
    {
        var filename = file.FileName;
        if (filename.IndexOfAny(['/', '\\', ':']) >= 0 || filename.Any(char.IsControl) || filename.Length > 255)
            throw new InvalidOperationException("Use a plain image filename without a path.");
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (extension is ".heic" or ".heif") throw new InvalidOperationException("HEIC/HEIF photos are not supported. Export or share the photo as JPEG, or choose Most Compatible in your camera settings.");
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) throw new InvalidOperationException("Choose a JPEG, PNG, or WebP image.");
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidOperationException("Images must be between 1 byte and 20 MB.");
        if ((altText ?? "").Length > 1000) throw new InvalidOperationException("Image descriptions must be at most 1000 characters.");
        using var buffer = new MemoryStream();
        await using (var stream = file.OpenReadStream())
        {
            var chunk = new byte[81920]; int count;
            while ((count = await stream.ReadAsync(chunk)) != 0)
            {
                if (buffer.Length + count > MaximumBytes) throw new InvalidOperationException("Images must be at most 20 MB.");
                await buffer.WriteAsync(chunk.AsMemory(0, count));
            }
        }
        buffer.Position = 0;
        int width, height; string mime;
        try
        {
            var format = Image.DetectFormat(buffer); buffer.Position = 0;
            mime = format.DefaultMimeType;
            var expected = extension switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
            if (mime != expected || (!string.IsNullOrEmpty(file.ContentType) && file.ContentType != mime))
                throw new InvalidOperationException("The file contents, extension, and image type must match.");
            var options = new DecoderOptions { MaxFrames = 1, SkipMetadata = true };
            var info = Image.Identify(options, buffer); buffer.Position = 0;
            if ((long)info.Width * info.Height > 40_000_000) throw new InvalidOperationException("Choose an image of at most 40 megapixels.");
            using var decoded = Image.Load(options, buffer); // Reject truncated/corrupt images, not just spoofed headers.
            width = decoded.Width; height = decoded.Height;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        { throw new InvalidOperationException("This image is damaged or unsupported. Export it as JPEG, PNG, or WebP.", ex); }

        await writer.Semaphore.WaitAsync();
        string? destination = null;
        try
        {
            var post = postId is { } id ? await articles.FindAsync(id) ?? throw new KeyNotFoundException() : null;
            var asset = new MediaAsset
            {
                StoredFilename = Guid.NewGuid().ToString("N") + (extension == ".jpeg" ? ".jpg" : extension),
                OriginalFilename = filename,
                MimeType = mime,
                Size = buffer.Length,
                Width = width,
                Height = height
            };
            var directory = Path.Combine(paths.OriginalsPath, asset.Id.ToString("D")); Directory.CreateDirectory(directory);
            destination = Path.Combine(directory, asset.StoredFilename);
            await File.WriteAllBytesAsync(destination, buffer.ToArray());
            if (post is not null) db.PostMedia.Add(new PostMedia
            {
                Post = post,
                MediaAsset = asset,
                Role = post.Type == PostType.Gallery ? MediaRole.Gallery : post.Type == PostType.Guide ? MediaRole.GuideStep : MediaRole.Inline,
                InlineOnly = inlineOnly,
                SortOrder = post.Media.Count == 0 ? 0 : post.Media.Max(x => x.SortOrder) + 1,
                AltText = altText
            });
            else
            {
                db.MediaAssets.Add(asset);
                var settings = await db.SiteSettings.SingleAsync();
                switch (slot)
                {
                    case "Avatar": settings.AvatarMediaId = asset.Id; break;
                    case "Logo": settings.LogoMediaId = asset.Id; break;
                    case "Header": settings.HeaderMediaId = asset.Id; break;
                }
            }
            await db.SaveChangesAsync();
            return new ArticleMedia(asset.Id, asset.StoredFilename, mime, altText ?? "");
        }
        catch { if (destination is not null) File.Delete(destination); throw; }
        finally { writer.Semaphore.Release(); }
    }

    public string OriginalPath(ArticleMedia media) => Path.Combine(paths.OriginalsPath, media.Id.ToString("D"), media.Filename);
}
