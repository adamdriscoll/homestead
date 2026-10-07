using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Articles;

public sealed class DraftConflictException : Exception;

public sealed class ArticleService(HomesteadDbContext db, PublicationLock writer)
{
    public Task<Post?> FindAsync(Guid id) => db.Posts.AsSplitQuery().Include(x => x.Draft)
        .Include(x => x.PublishedSnapshot).Include(x => x.Tags).ThenInclude(x => x.Tag)
        .Include(x => x.Media).ThenInclude(x => x.MediaAsset)
        .SingleOrDefaultAsync(x => x.Id == id && x.Type == PostType.Article && x.Status != PostStatus.Deleted);

    public async Task<Post> CreateAsync()
    {
        var post = new Post { Type = PostType.Article, Draft = new PostDraft() };
        db.Posts.Add(post); await db.SaveChangesAsync(); return post;
    }

    public async Task<long> SaveAsync(Guid id, ArticleInput input)
    {
        await writer.Semaphore.WaitAsync();
        try
        {
            var post = await FindAsync(id) ?? throw new KeyNotFoundException();
            if (post.Draft!.Version != input.Version) throw new DraftConflictException();
            if (input.CoverMediaId is { } cover && !post.Media.Any(x => x.MediaAssetId == cover))
                throw new InvalidOperationException("Choose a cover image uploaded to this article.");
            if (input.AltText.Values.Any(x => x?.Length > 1000)) throw new InvalidOperationException("Image descriptions must be at most 1000 characters.");
            post.Draft.Title = input.Title; post.Draft.BodyMarkdown = input.BodyMarkdown ?? "";
            post.Draft.Summary = input.Summary; post.Draft.CoverMediaId = input.CoverMediaId;
            post.Draft.Version++; post.UpdatedAtUtc = DateTime.UtcNow;
            foreach (var media in post.Media)
                if (input.AltText.TryGetValue(media.MediaAssetId, out var alt)) media.AltText = alt;
            var names = (input.Tags ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Normalize()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > 20 || names.Any(x => x.Length > 80)) throw new InvalidOperationException("Use up to 20 tags, each at most 80 characters.");
            db.PostTags.RemoveRange(post.Tags); post.Tags.Clear();
            foreach (var name in names)
            {
                var normalized = name.Normalize().ToUpperInvariant();
                var tag = await db.Tags.SingleOrDefaultAsync(x => x.NormalizedName == normalized);
                if (tag is null)
                {
                    var root = Slugs.Create(name); var slug = root; var suffix = 2;
                    while (await db.Tags.AnyAsync(x => x.Slug == slug) || db.Tags.Local.Any(x => x.Slug == slug)) slug = $"{root}-{suffix++}";
                    tag = new Tag { Name = name, Slug = slug }; db.Tags.Add(tag);
                }
                post.Tags.Add(new PostTag { Tag = tag });
            }
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { throw new DraftConflictException(); }
            return post.Draft.Version;
        }
        finally { writer.Semaphore.Release(); }
    }

    public static ArticleSnapshot Snapshot(Post post, string slug, DateTime now) => new(1, post.Id, slug,
        post.Draft!.Title ?? "", post.Draft.BodyMarkdown, post.Draft.Summary,
        post.FirstPublishedAtUtc ?? now, now, post.Draft.Version,
        post.Tags.Select(x => x.Tag.Name).ToArray(), post.Draft.CoverMediaId,
        post.Media.OrderBy(x => x.SortOrder).Select(x => new ArticleMedia(x.MediaAssetId,
            x.MediaAsset.StoredFilename, x.MediaAsset.MimeType, x.AltText ?? "")).ToArray());

    public static bool HasDraftChanges(Post post, ArticleSnapshot live) => post.Draft is { } draft &&
        (draft.Title != live.Title || draft.BodyMarkdown != live.BodyMarkdown || draft.Summary != live.Summary ||
         draft.CoverMediaId != live.CoverMediaId ||
         !post.Tags.Select(x => x.Tag.Name).Order(StringComparer.Ordinal).SequenceEqual(live.Tags.Order(StringComparer.Ordinal)) ||
         live.Media.Any(x => (post.Media.SingleOrDefault(m => m.MediaAssetId == x.Id)?.AltText ?? "") != x.AltText));
}
