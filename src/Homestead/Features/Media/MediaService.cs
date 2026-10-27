using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Publishing;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Media;

public sealed class MediaService(HomesteadDbContext db, ArticleService articles, PublicationLock writer, StoragePaths paths)
{
    public const long MaximumBytes = ImageUploadValidator.MaximumBytes;
    public const int MaximumFiles = 50;
    public Task<ArticleMedia> UploadAsync(Guid postId, IFormFile file, string? altText, bool inlineOnly = false, PostType? newPostType = null, bool retainOriginal = false)
        => StoreAsync(postId, file, altText, null, inlineOnly, newPostType, retainOriginal);

    public Task<ArticleMedia> UploadBrandingAsync(string slot, IFormFile file)
    {
        if (slot is not ("Avatar" or "Logo" or "Header" or "Favicon")) throw new InvalidOperationException("Choose an avatar, logo, header, or favicon image.");
        return StoreAsync(null, file, "", slot);
    }

    private async Task<ArticleMedia> StoreAsync(Guid? postId, IFormFile file, string? altText, string? slot, bool inlineOnly = false, PostType? newPostType = null, bool retainOriginal = false)
    {
        var filename = file.FileName;
        if (filename.IndexOfAny(['/', '\\', ':']) >= 0 || filename.Any(char.IsControl) || filename.Length > 255)
            throw new InvalidOperationException("Use a plain image filename without a path.");
        var extension = Path.GetExtension(filename).ToLowerInvariant();
        if (extension is ".heic" or ".heif") throw new InvalidOperationException("HEIC/HEIF photos are not supported. Export or share the photo as JPEG, or choose Most Compatible in your camera settings.");
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")) throw new InvalidOperationException("Choose a JPEG, PNG, or WebP image.");
        if (file.Length is <= 0 or > MaximumBytes) throw new InvalidOperationException("Images must be between 1 byte and 20 MB.");
        if ((altText ?? "").Length > 1000) throw new InvalidOperationException("Image descriptions must be at most 1000 characters.");
        await writer.Semaphore.WaitAsync();
        string? destination = null;
        string? retainedDestination = null;
        try
        {
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
            var original = buffer.ToArray();
            OptimizedImage stored;
            if (postId is not null) stored = ImageOptimizer.Optimize(original, extension, file.ContentType);
            else
            {
                var (width, height, mime) = ImageUploadValidator.Validate(original, extension, file.ContentType);
                stored = new(original, extension == ".jpeg" ? ".jpg" : extension, mime, width, height);
            }
            var post = postId is { } id ? await articles.FindOrNewAsync(id, newPostType) ?? throw new KeyNotFoundException() : null;
            var asset = new MediaAsset
            {
                StoredFilename = Guid.NewGuid().ToString("N") + stored.Extension,
                OriginalFilename = filename,
                RetainedOriginalFilename = retainOriginal ? Guid.NewGuid().ToString("N") + extension : null,
                RetainedOriginalSize = retainOriginal ? original.LongLength : null,
                MimeType = stored.MimeType,
                Size = stored.Bytes.LongLength,
                Width = stored.Width,
                Height = stored.Height
            };
            var directory = Path.Combine(paths.OriginalsPath, asset.Id.ToString("D")); Directory.CreateDirectory(directory);
            destination = Path.Combine(directory, asset.StoredFilename);
            await File.WriteAllBytesAsync(destination, stored.Bytes);
            if (asset.RetainedOriginalFilename is { } retainedFilename)
            {
                retainedDestination = Path.Combine(directory, retainedFilename);
                await File.WriteAllBytesAsync(retainedDestination, original);
            }
            if (post is not null && db.Entry(post).State == EntityState.Detached) db.Posts.Add(post);
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
                    case "Favicon": settings.FaviconMediaId = asset.Id; break;
                }
            }
            await db.SaveChangesAsync();
            return new ArticleMedia(asset.Id, asset.StoredFilename, stored.MimeType, altText ?? "");
        }
        catch
        {
            if (destination is not null) File.Delete(destination);
            if (retainedDestination is not null) File.Delete(retainedDestination);
            throw;
        }
        finally { writer.Semaphore.Release(); }
    }

    public string OriginalPath(ArticleMedia media) => Path.Combine(paths.OriginalsPath, media.Id.ToString("D"), media.Filename);
}
