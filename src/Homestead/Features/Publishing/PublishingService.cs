using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Publishing;

public sealed record PublicationResult(bool Succeeded, string Message, string? Url = null);

public sealed class PublishingService(HomesteadDbContext db, ArticleService articles, PublicationLock writer,
    IStaticPageRenderer renderer, SafeMarkdown markdown, MediaService media, StoragePaths paths,
    ActiveSite active, IWebHostEnvironment environment, ILogger<PublishingService> logger)
{
    public async Task<PublicationResult> TransitionAsync(Guid id, string action, long version)
    {
        await writer.Semaphore.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var post = await articles.FindAsync(id) ?? throw new KeyNotFoundException();
            if (post.Draft!.Version != version) throw new DraftConflictException();
            if (action == "publish")
            {
                if (string.IsNullOrWhiteSpace(post.Draft.Title) || string.IsNullOrWhiteSpace(post.Draft.BodyMarkdown))
                    return new(false, "An article needs a title and body before publication.");
                var now = DateTime.UtcNow;
                var slug = post.Slug ?? Slugs.Create(post.Draft.Title); var root = slug; var suffix = 2;
                while (await db.Posts.AnyAsync(x => x.Slug == slug && x.Id != id)) slug = $"{root}-{suffix++}";
                var snapshot = ArticleService.Snapshot(post, slug, now);
                try { snapshot = snapshot with { Media = markdown.ReferencedMedia(snapshot) }; }
                catch (InvalidOperationException ex) { return new(false, ex.Message); }
                post.Slug = slug; post.FirstPublishedAtUtc ??= now; post.LastPublishedAtUtc = now;
                post.Status = PostStatus.Published;
                post.PublishedSnapshot ??= new PostPublishedSnapshot();
                post.PublishedSnapshot.SnapshotJson = JsonSerializer.Serialize(snapshot);
                post.PublishedSnapshot.PublishedAtUtc = now; post.PublishedSnapshot.IsActive = true;
            }
            else if (action is "unpublish" or "delete")
            {
                post.Status = action == "delete" ? PostStatus.Deleted : PostStatus.Unpublished;
                if (post.PublishedSnapshot is { } snapshot) snapshot.IsActive = false;
            }
            else throw new InvalidOperationException("Unknown publication action.");
            await db.SaveChangesAsync();
            var result = await BuildLockedAsync();
            var stillLive = active.Current?.Articles.Any(x => x.PostId == id) == true;
            return result.Succeeded ? result with
            {
                Message = action switch
                { "publish" => "Article published. Your public site is active.", "unpublish" => "Article unpublished.", _ => "Article deleted." },
                Url = action == "publish" ? $"/posts/{post.Slug}/" : null
            }
                : result with { Message = result.Message + (stillLive ? " The previous article is still public." : " This article is not public.") };
        }
        finally { writer.Semaphore.Release(); }
    }

    public async Task<PublicationResult> RebuildAsync()
    {
        await writer.Semaphore.WaitAsync();
        try { return await BuildLockedAsync(); }
        finally { writer.Semaphore.Release(); }
    }

    // Caller holds the publication lock, including when saving settings.
    public async Task<PublicationResult> BuildLockedAsync()
    {
        var generation = new SiteGeneration { BuiltAtUtc = DateTime.UtcNow, Status = GenerationStatus.Building };
        generation.OutputRoot = generation.Id.ToString("N");
        db.SiteGenerations.Add(generation); await db.SaveChangesAsync();
        var root = Path.Combine(paths.BuildsPath, generation.OutputRoot);
        var activated = false;
        try
        {
            var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
            var snapshots = await db.PublishedSnapshots.AsNoTracking()
                .Where(x => x.IsActive && x.Post.Status == PostStatus.Published).Select(x => x.SnapshotJson).ToListAsync();
            var posts = snapshots.Select(x => JsonSerializer.Deserialize<ArticleSnapshot>(x) ?? throw new InvalidOperationException("Invalid article snapshot."))
                .OrderByDescending(x => x.PublishedAtUtc).ToArray();
            Directory.CreateDirectory(root); var files = new List<string>();
            async Task Write(string relative, string html)
            {
                if (!html.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) || !html.Contains("</html>", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The renderer did not produce a complete HTML document.");
                var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, html); files.Add(relative);
            }
            await Write("index.html", await renderer.RenderAsync(new(settings, null, posts)));
            foreach (var post in posts)
            {
                if (post.SchemaVersion != 1 || Slugs.Create(post.Slug) != post.Slug) throw new InvalidOperationException("Invalid article snapshot slug or schema.");
                await Write($"posts/{post.Slug}/index.html", await renderer.RenderAsync(new(settings, post, [])));
            }
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            File.Copy(Path.Combine(environment.WebRootPath, "css", "site.css"), Path.Combine(root, "assets", "site.css"));
            files.Add("assets/site.css");
            foreach (var asset in posts.SelectMany(x => x.Media).DistinctBy(x => x.Id))
            {
                var relative = asset.Url.TrimStart('/'); var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(media.OriginalPath(asset), destination); files.Add(relative);
            }
            var manifest = new GenerationManifest(generation.Id, DateTime.UtcNow, settings, posts, files.ToArray());
            var temporary = Path.Combine(paths.SitePath, $"current-{generation.Id:N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, manifest); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, paths.CurrentPath, overwrite: true); // Same-volume atomic replacement.
            active.Activate(manifest); activated = true;
            generation.Status = GenerationStatus.Succeeded; generation.ActivatedAtUtc = manifest.ActivatedAtUtc;
            await db.SaveChangesAsync();
            await PruneAsync();
            return new(true, "Public site rebuilt successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Site generation {GenerationId} failed; activated: {Activated}.", generation.Id, activated);
            if (activated) return new(true, "Public site is active. Build record will be reconciled on restart.");
            generation.Status = GenerationStatus.Failed; generation.Error = "Generation failed. Check server logs and retry the rebuild.";
            try { await db.SaveChangesAsync(); } catch (Exception recordError) { logger.LogError(recordError, "Unable to save failed generation record."); }
            try { active.RemoveBuild(generation.Id); } catch (IOException cleanupError) { logger.LogWarning(cleanupError, "Unable to clean failed generation."); }
            return new(false, "Site generation failed. The previous public site remains active. Retry with Rebuild site.");
        }
    }

    private async Task PruneAsync()
    {
        try
        {
            var keep = await db.SiteGenerations.AsNoTracking().Where(x => x.Status == GenerationStatus.Succeeded)
                .OrderByDescending(x => x.BuiltAtUtc).Take(3).Select(x => x.Id).ToListAsync();
            foreach (var directory in Directory.GetDirectories(paths.BuildsPath))
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && !keep.Contains(id)) active.RemoveBuild(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning(ex, "Unable to prune old generations; active generation is unchanged."); }
    }
}
