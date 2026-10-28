using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Export;
using Homestead.Features.Media;
using Homestead.Features.Newsletter;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Publishing;

public sealed record PublicationResult(bool Succeeded, string Message, string? Url = null);

public sealed partial class PublishingService(HomesteadDbContext db, ArticleService articles, PublicationLock writer,
    IStaticPageRenderer renderer, SafeMarkdown markdown, MediaService media, StoragePaths paths,
    ActiveSite active, IGenerationActivation activation, ThemeService themes, IWebHostEnvironment environment, ILogger<PublishingService> logger,
    AutomatedBackupService automatedBackups, NewsletterService newsletters, TimeProvider clock)
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
                if (!(await db.SiteSettings.AsNoTracking().SingleAsync()).IsPostTypeEnabled(post.Type))
                    return new(false, "This post type is disabled. Enable it in Settings before publishing.");
                if (PostValidation.PublicationError(post) is { } error) return new(false, error);
                ArticleSnapshot snapshot;
                try { snapshot = await PrepareSnapshotAsync(post, clock.GetUtcNow().UtcDateTime); }
                catch (InvalidOperationException ex) { return new(false, ex.Message); }
                CommitSnapshot(post, snapshot);
            }
            else if (action is "unpublish" or "delete")
            {
                post.Status = action == "delete" ? PostStatus.Deleted : PostStatus.Unpublished;
                if (post.PublishedSnapshot is { } snapshot) snapshot.IsActive = false;
            }
            else throw new InvalidOperationException("Unknown publication action.");
            if (post.Schedule is { } schedule) { db.PostSchedules.Remove(schedule); post.Schedule = null; }
            await db.SaveChangesAsync();
            var result = await BuildLockedAsync();
            var stillLive = active.Current?.Articles.Any(x => x.PostId == id) == true;
            return result.Succeeded ? result with
            {
                Message = action switch
                { "publish" => "Post published. Your public site is active.", "unpublish" => "Post unpublished.", _ => "Post deleted." },
                Url = action == "publish" ? $"/posts/{post.Slug}/" : null
            }
                : result with { Message = result.Message + (stillLive ? " The previous post is still public." : " This post is not public.") };
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
        var backup = await automatedBackups.RunLockedAsync(beforePublication: true);
        if (!backup.Succeeded) return new(false, backup.Message + " The previous public site remains active; saved changes are pending. Retry the backup or disable automated backups, then rebuild.");
        var generation = new SiteGeneration { BuiltAtUtc = DateTime.UtcNow, Status = GenerationStatus.Building };
        generation.OutputRoot = generation.Id.ToString("N");
        db.SiteGenerations.Add(generation); await db.SaveChangesAsync();
        var root = Path.Combine(paths.BuildsPath, generation.OutputRoot);
        var activated = false;
        try
        {
            var settings = await db.SiteSettings.AsNoTracking().SingleAsync();
            var theme = themes.Resolve(settings.ThemeId);
            var snapshots = await db.PublishedSnapshots.AsNoTracking()
                .Where(x => x.IsActive && x.Post.Status == PostStatus.Published).Select(x => x.SnapshotJson).ToListAsync();
            var posts = snapshots.Select(x => JsonSerializer.Deserialize<ArticleSnapshot>(x) ?? throw new InvalidOperationException("Invalid article snapshot."))
                .Where(x => settings.IsPostTypeEnabled(x.Type))
                .OrderByDescending(x => x.PublishedAtUtc).ThenBy(x => x.PostId).ToArray();
            if (posts.Any(x => x.Tags.Length > 0 && x.TagSlugs.Count == 0))
            {
                var tags = await db.Tags.AsNoTracking().ToArrayAsync();
                posts = posts.Select(post => post.TagSlugs.Count > 0 ? post : post with
                {
                    TagSlugs = post.Tags.ToDictionary(name => name, name => tags.SingleOrDefault(tag =>
                        tag.NormalizedName == name.Normalize().ToUpperInvariant())?.Slug ?? Slugs.Create(name))
                }).ToArray();
            }
            var brandingIds = new[] { settings.AvatarMediaId, settings.LogoMediaId, settings.HeaderMediaId, settings.FaviconMediaId }.OfType<Guid>().ToArray();
            var branding = (await db.MediaAssets.AsNoTracking().Where(x => brandingIds.Contains(x.Id)).ToArrayAsync())
                .Select(x => new ArticleMedia(x.Id, x.StoredFilename, x.MimeType, settings.Title)).ToArray();
            if (branding.Length != brandingIds.Distinct().Count()) throw new InvalidOperationException("A branding image is missing.");
            Directory.CreateDirectory(root); var files = new List<string>();
            var pagePaths = new List<string>();
            async Task Write(string relative, string html)
            {
                if (!html.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) || !html.Contains("</html>", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The renderer did not produce a complete HTML document.");
                var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, html); files.Add(relative);
            }
            async Task Page(string path, string heading, ArticleSnapshot[] items, ArticleSnapshot? post = null, Guid? galleryImageId = null)
            {
                await Write(path.TrimStart('/') + "index.html", await renderer.RenderAsync(new(settings, post, items,
                    Path: path, Heading: heading, Branding: branding, Theme: theme)
                { GalleryImageId = galleryImageId }));
                pagePaths.Add(path);
            }
            await Page("/", "Welcome home.", posts);
            foreach (var (type, path, heading) in new[] { (PostType.Article, "/articles/", "Articles"),
                (PostType.Microblog, "/notes/", "Notes"), (PostType.Review, "/reviews/", "Reviews"),
                (PostType.Guide, "/guides/", "Guides"), (PostType.Gallery, "/galleries/", "Galleries"),
                (PostType.Checkin, "/checkins/", "Check-ins"), (PostType.Fitness, "/fitness/", "Fitness activities") })
                if (settings.IsPostTypeEnabled(type)) await Page(path, heading, posts.Where(x => x.Type == type).ToArray());
            await Page("/about/", "About", []);
            if (settings.EnableLocationMap)
                await Page("/map/", "Location map", posts);
            foreach (var tag in posts.SelectMany(post => post.Tags.Select(name => (Name: name, Slug: post.TagSlug(name))))
                .DistinctBy(x => x.Slug))
            {
                if (Slugs.Create(tag.Slug) != tag.Slug) throw new InvalidOperationException("Invalid tag slug.");
                await Page($"/tags/{tag.Slug}/", $"Tagged: {tag.Name}", posts.Where(post => post.Tags.Any(name => post.TagSlug(name) == tag.Slug)).ToArray());
            }
            foreach (var post in posts)
            {
                if (post.SchemaVersion != 1 || Slugs.Create(post.Slug) != post.Slug) throw new InvalidOperationException("Invalid article snapshot slug or schema.");
                await Page($"/posts/{post.Slug}/", post.DisplayTitle, [], post);
                if (post.Type == PostType.Gallery)
                    foreach (var image in post.Media)
                        await Page(GalleryImages.Path(post, image), GalleryImages.Title(post, image), [], post, image.Id);
            }
            async Task DataFile(string relative, string contents)
            { await File.WriteAllTextAsync(Path.Combine(root, relative), contents); files.Add(relative); }
            await DataFile("rss.xml", DiscoveryFiles.Rss(settings, posts, markdown));
            await DataFile("sitemap.xml", DiscoveryFiles.Sitemap(settings, pagePaths));
            await DataFile("robots.txt", $"User-agent: *\nAllow: /\nSitemap: {DiscoveryFiles.Absolute(settings, "/sitemap.xml")}\n");
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            File.Copy(Path.Combine(environment.WebRootPath, "css", "site.css"), Path.Combine(root, "assets", "site.css"));
            files.Add("assets/site.css");
            if (settings.FaviconMediaId is null || !settings.HideHomesteadCredit)
            {
                File.Copy(Path.Combine(environment.WebRootPath, "logo.png"), Path.Combine(root, "assets", "favicon.png"));
                files.Add("assets/favicon.png");
            }
            if (settings.EnableLocationMap || posts.Any(x => x.Type == PostType.Fitness && x.Activity is not null))
            {
                foreach (var (relative, source) in LocationMapAssets.Files(environment.ContentRootPath))
                {
                    var destination = Path.Combine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination); files.Add(relative);
                }
            }
            foreach (var (file, contents) in theme.Files.Where(x => !ThemePackage.IsSource(x.Key)))
            {
                var relative = "assets/theme/" + file;
                var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, contents); files.Add(relative);
            }
            await DataFile("assets/theme-colors.css", ThemeService.ColorStyles(settings, theme));
            File.Copy(Path.Combine(environment.WebRootPath, "social-default.png"), Path.Combine(root, "assets", "social-default.png"));
            files.Add("assets/social-default.png");
            foreach (var asset in posts.SelectMany(x => x.Media).Concat(branding).DistinctBy(x => x.Id))
            {
                var relative = asset.Url.TrimStart('/'); var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(media.OriginalPath(asset), destination); files.Add(relative);
            }
            var manifest = new GenerationManifest(generation.Id, DateTime.UtcNow, settings, posts, files.ToArray(), branding, theme.Snapshot);
            await activation.ActivateAsync(manifest); activated = true;
            try { await newsletters.CaptureLockedAsync(manifest); }
            catch (Exception ex) { logger.LogWarning("Newsletter activation recording failed ({FailureType}); the worker will retry.", ex.GetType().Name); }
            await CompleteActiveSchedulesLockedAsync();
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
