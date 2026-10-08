using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Articles;
using Homestead.Features.Media;
using Homestead.Features.Publishing;
using Homestead.Features.Themes;
using Homestead.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Export;

public sealed class HugoExportService(HomesteadDbContext db, ArticleService articles, MediaService media,
    StoragePaths paths, PublicationLock writer, ActiveSite active, IWebHostEnvironment environment, ThemeService themes)
{
    public const string SupportedHugoVersion = "0.152.2";

    public Task<FileStream> CreateAsync(CancellationToken ct = default) => ArchiveFiles.CreateAsync(paths, writer, async zip =>
    {
        db.ChangeTracker.Clear();
        var settings = await db.SiteSettings.AsNoTracking().SingleAsync(ct);
        var theme = themes.Resolve(settings.ThemeId);
        await ArchiveFiles.TextAsync(zip, "hugo.toml", $"baseURL = {TomlString(settings.CanonicalBaseUrl.TrimEnd('/') + "/")}\n" +
            $"title = {TomlString(settings.Title)}\nlanguageCode = 'en-us'\nenableRobotsTXT = true\n" +
            "[markup.goldmark.renderer]\nunsafe = false\n", ct);
        foreach (var (path, text) in HugoTemplates.Files)
            await ArchiveFiles.TextAsync(zip, path, path == "layouts/baseof.html" ? HugoTemplates.ThemedLayout(settings, theme) : text, ct);
        await ArchiveFiles.CopyAsync(zip, "static/assets/site.css", Path.Combine(environment.WebRootPath, "css", "site.css"), ct);
        foreach (var (file, bytes) in theme.Files)
        {
            await using (var output = zip.CreateEntry("homestead-theme/" + file).Open())
                await output.WriteAsync(bytes, ct);
            if (file == "theme.json" || file.EndsWith(".html", StringComparison.Ordinal)) continue;
            await using var asset = zip.CreateEntry("static/assets/theme/" + file).Open();
            await asset.WriteAsync(bytes, ct);
        }
        await ArchiveFiles.TextAsync(zip, "static/assets/theme-colors.css", ThemeService.ColorStyles(settings, theme), ct);
        await ArchiveFiles.TextAsync(zip, "content/about/index.md", "---\ntitle: \"About\"\nurl: \"/about/\"\n---\n" +
            settings.AboutMarkdown.Replace("{{", "&#123;&#123;", StringComparison.Ordinal), ct);
        var ids = await db.Posts.Where(x => x.Status != PostStatus.Deleted).OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).ToArrayAsync(ct);
        var usedSlugs = (await db.Posts.Where(x => x.Slug != null).Select(x => x.Slug!).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string PrivateSlug(string root)
        {
            var slug = root; var suffix = 2;
            while (!usedSlugs.Add(slug)) slug = $"{root}-{suffix++}";
            return slug;
        }
        foreach (var id in ids)
        {
            var post = (await articles.FindAsync(id))!;
            var slug = post.Slug ?? PrivateSlug($"draft-{post.Id:N}");
            var working = ArticleService.Snapshot(post, slug, post.UpdatedAtUtc);
            var live = active.Current?.Articles.SingleOrDefault(x => x.PostId == id);
            // Only an actually public snapshot builds by default. Unpublished work is portable but remains a draft.
            if (post.Status == PostStatus.Published && live is not null)
            {
                await Bundle(live, false, false, slug);
                if (ArticleService.HasDraftChanges(post, live) || post.Media.Any(image => live.Media.All(x => x.Id != image.MediaAssetId)))
                    await Bundle(working, true, false, PrivateSlug($"{slug}-working-{id:N}"));
            }
            else await Bundle(working, true, post.Status == PostStatus.Unpublished, slug);
        }
        await ArchiveFiles.TextAsync(zip, "README.md", $"# Homestead Hugo export\n\nTested with Hugo {SupportedHugoVersion} (standard or extended). Run `hugo` in this directory; serve `public/`.\n\n" +
            "All nondeleted posts are included. Active published snapshots build by default. Drafts, unpublished posts, pending publications, and separate working bundles containing unpublished edits use `draft: true`. Use `hugo --buildDrafts` only to preview private work.\n\n" +
            "Each bundle includes `homestead_id`, `type`, `date`, `lastmod`, `url`, `tags`, `summary`, `cover`, ordered `images` (file/alt/caption/step_id), `review` (subject/rating/external_url), and ordered `steps` (id/body_markdown/images). Steps and captions also appear in readable Markdown. `homestead_unpublished` records unpublished status. Working bundles use a private preview URL. Media uses local bundle filenames.\n\n" +
            "Raw HTML is disabled. This minimal layout renders all five types and carries the selected theme assets, layout, and colors. The original package files are in homestead-theme/. It is a migration starting point. Keep this archive private: it includes drafts and original photos.\n", ct);

        async Task Bundle(ArticleSnapshot snapshot, bool draft, bool unpublished, string bundleSlug)
        {
            if (string.IsNullOrEmpty(bundleSlug) || bundleSlug.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                throw new InvalidDataException("Invalid export slug.");
            var directory = $"content/posts/{bundleSlug}/";
            _ = ArchiveFiles.SafePath(paths.DataRoot, directory + "index.md");
            string Rewrite(string text)
            {
                foreach (var image in snapshot.Media)
                    text = text.Replace(image.Url, image.Filename, StringComparison.Ordinal).Replace(image.PrivateUrl, image.Filename, StringComparison.Ordinal);
                // Preserve literal user text instead of interpreting it as Hugo shortcodes.
                return text.Replace("{{", "&#123;&#123;", StringComparison.Ordinal);
            }
            var images = snapshot.Media.Select(x => new { file = x.Filename, alt = x.AltText, caption = x.Caption, step_id = x.GuideStepId }).ToArray();
            var frontMatter = new
            {
                title = snapshot.DisplayTitle,
                date = snapshot.PublishedAtUtc.ToUniversalTime().ToString("O"),
                lastmod = snapshot.ModifiedAtUtc.ToUniversalTime().ToString("O"),
                draft,
                tags = snapshot.Tags,
                type = snapshot.Type.ToString().ToLowerInvariant(),
                url = $"/posts/{bundleSlug}/",
                homestead_id = snapshot.PostId,
                homestead_unpublished = unpublished,
                summary = snapshot.Summary,
                cover = snapshot.Media.SingleOrDefault(x => x.Id == snapshot.CoverMediaId)?.Filename,
                images,
                review = snapshot.Review is { } review ? new { subject = review.Subject, rating = review.Rating, external_url = review.ExternalUrl } : null,
                steps = snapshot.Steps.Select(step => new
                {
                    id = step.Id,
                    body_markdown = Rewrite(step.BodyMarkdown),
                    images = images.Where(x => x.step_id == step.Id).ToArray()
                }).ToArray()
            };
            // JSON front matter is supported by Hugo and avoids YAML injection/Unicode ambiguity.
            var body = Rewrite(snapshot.BodyMarkdown);
            static string Escape(string? text) => (text ?? "").Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
                .Replace("*", "\\*").Replace("_", "\\_").Replace("<", "&lt;").Replace(">", "&gt;").Replace("{{", "&#123;&#123;");
            string Photo(ArticleMedia image) => $"\n\n![{Escape(image.AltText)}]({image.Filename})\n\n{Escape(image.Caption)}\n";
            if (snapshot.Review is { } details)
                body = $"**{Escape(details.Subject)}** — {details.Rating}/5\n\n" +
                    (PostValidation.IsExternalUrl(details.ExternalUrl) && !string.IsNullOrWhiteSpace(details.ExternalUrl)
                        ? $"[Visit reviewed subject](<{details.ExternalUrl}>)\n\n" : "") + body;
            if (snapshot.Type == PostType.Guide)
            {
                foreach (var (step, order) in snapshot.Steps.Select((step, order) => (step, order)))
                    body += $"\n\n## Step {order + 1}\n\n{Rewrite(step.BodyMarkdown)}" +
                        string.Concat(snapshot.Media.Where(x => x.GuideStepId == step.Id).Select(Photo));
                body += string.Concat(snapshot.Media.Where(x => x.GuideStepId is null).Select(Photo));
            }
            else body += string.Concat(snapshot.Media.Where(x => !snapshot.BodyMarkdown.Contains(x.Url, StringComparison.Ordinal)).Select(Photo));
            await ArchiveFiles.TextAsync(zip, directory + "index.md", JsonSerializer.Serialize(frontMatter, new JsonSerializerOptions { WriteIndented = true }) + "\n\n" + body, ct);
            foreach (var image in snapshot.Media)
            {
                _ = ArchiveFiles.SafePath(paths.DataRoot, directory + image.Filename);
                await ArchiveFiles.CopyAsync(zip, directory + image.Filename, media.OriginalPath(image), ct);
            }
        }
    }, ct);

    // TOML disallows JSON's escaped UTF-16 surrogate pairs; preserve non-ASCII text as UTF-8.
    private static string TomlString(string text) => "\"" + string.Concat(text.Select(c => c switch
    {
        '\\' => "\\\\",
        '"' => "\\\"",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ when char.IsControl(c) => $"\\u{(int)c:x4}",
        _ => c.ToString()
    })) + "\"";
}
