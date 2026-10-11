using System.Text.Json;
using Homestead.Data;
using Homestead.Data.Entities;
using Homestead.Features.Publishing;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Features.Articles;

public sealed class DraftConflictException : Exception;

public sealed class ArticleService(HomesteadDbContext db, PublicationLock writer)
{
    public Task<Post?> FindAsync(Guid id) => db.Posts.AsSplitQuery().Include(x => x.Draft)
        .Include(x => x.PublishedSnapshot).Include(x => x.Schedule).Include(x => x.Tags).ThenInclude(x => x.Tag)
        .Include(x => x.Media).ThenInclude(x => x.MediaAsset)
        .Include(x => x.Review).Include(x => x.Activity).Include(x => x.Steps).Include(x => x.Supplies)
        .SingleOrDefaultAsync(x => x.Id == id && x.Status != PostStatus.Deleted);

    public async Task<Post> CreateAsync(PostType type = PostType.Article)
    {
        var post = NewEditor(type);
        db.Posts.Add(post); await db.SaveChangesAsync(); return post;
    }

    public async Task<long> ImportGpxAsync(Guid id, long version, IFormFile? upload, PostType? newPostType = null, bool remove = false,
        CancellationToken ct = default)
    {
        byte[] bytes = [];
        FitnessSnapshot? snapshot = null;
        if (!remove)
        {
            if (upload is null || !upload.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase) || upload.Length is <= 0 or > GpxParser.MaximumBytes)
                throw new InvalidOperationException("Choose a GPX file up to 10 MB.");
            using var buffer = new MemoryStream();
            await using var source = upload.OpenReadStream();
            var chunk = new byte[81920]; int read;
            while ((read = await source.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > GpxParser.MaximumBytes) throw new InvalidOperationException("Choose a GPX file up to 10 MB.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            bytes = buffer.ToArray(); snapshot = GpxParser.Parse(bytes);
        }
        await writer.Semaphore.WaitAsync(ct);
        try
        {
            db.ChangeTracker.Clear();
            var post = await FindOrNewAsync(id, newPostType) ?? throw new KeyNotFoundException();
            if (post.Type != PostType.Fitness) throw new InvalidOperationException("GPX files belong to fitness activity posts.");
            if (post.Draft!.Version != version) throw new DraftConflictException();
            if (remove)
            {
                if (post.Activity is null) return version;
                db.FitnessActivities.Remove(post.Activity); post.Activity = null;
            }
            else
            {
                if (db.Entry(post).State == EntityState.Detached) db.Posts.Add(post);
                post.Activity ??= new FitnessActivity();
                post.Activity.OriginalGpx = bytes;
                post.Activity.TrackJson = JsonSerializer.Serialize(snapshot);
            }
            post.Draft.Version++; post.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return post.Draft.Version;
        }
        finally { writer.Semaphore.Release(); }
    }

    public static Post NewEditor(PostType type, Guid? id = null)
    {
        if (!Enum.IsDefined(type)) throw new InvalidOperationException("Choose a supported post type.");
        return new Post { Id = id ?? Guid.NewGuid(), Type = type, Draft = new PostDraft() };
    }

    // Reserve an editor ID without writing anything. Deleted posts cannot be recreated.
    public async Task<Post?> FindOrNewAsync(Guid id, PostType? newPostType)
    {
        var post = await FindAsync(id);
        if (post is not null || newPostType is null || id == Guid.Empty || await db.Posts.AnyAsync(x => x.Id == id)) return post;
        if (!(await db.SiteSettings.AsNoTracking().SingleAsync()).IsPostTypeEnabled(newPostType.Value)) return null;
        return NewEditor(newPostType.Value, id);
    }

    public async Task<long> SaveAsync(Guid id, ArticleInput input, PostType? newPostType = null)
    {
        await writer.Semaphore.WaitAsync();
        try
        {
            db.ChangeTracker.Clear();
            var post = await FindOrNewAsync(id, newPostType) ?? throw new KeyNotFoundException();
            var isNew = db.Entry(post).State == EntityState.Detached;
            if (post.Draft!.Version != input.Version) throw new DraftConflictException();
            if (PostValidation.LocationError(input) is { } locationError) throw new InvalidOperationException(locationError);
            if (input.CoverMediaId is { } cover && !post.Media.Any(x => x.MediaAssetId == cover))
                throw new InvalidOperationException("Choose a cover image uploaded to this article.");
            if (input.AltText.Values.Any(x => x?.Length > 1000)) throw new InvalidOperationException("Image descriptions must be at most 1000 characters.");
            if (input.Captions.Values.Any(x => x?.Length > 1000)) throw new InvalidOperationException("Captions must be at most 1000 characters.");
            if (post.Type == PostType.Review && (input.Subject?.Length > 300 || input.ExternalUrl?.Length > 2000 || input.Rating is < 1 or > 5 || !PostValidation.IsExternalUrl(input.ExternalUrl)))
                throw new InvalidOperationException("Use a subject of at most 300 characters, a rating from 1 to 5, and an absolute HTTP or HTTPS URL without credentials.");
            if (input.Steps.Count > 100 || input.Steps.Any(x => x.Id == Guid.Empty || x.Title?.Length > 300 || x.BodyMarkdown.Length > 200_000) || input.Steps.Select(x => x.Id).Distinct().Count() != input.Steps.Count)
                throw new InvalidOperationException("Use up to 100 distinct steps, with titles of at most 300 characters and instructions of at most 200000 characters.");
            if (post.Type == PostType.Guide)
            {
                if (input.Steps.Any(x => x.ReplacementDuration is { } replacement && (replacement < TimeSpan.Zero || replacement >= TimeSpan.FromHours(24)) ||
                    x.EffectiveDuration is { } time && (time <= TimeSpan.Zero || time.Ticks % TimeSpan.TicksPerSecond != 0 ||
                        time >= TimeSpan.FromHours(24) && !post.Steps.Any(existing => existing.Id == x.Id && existing.DurationSeconds == x.DurationSeconds)) ||
                    x.DurationSeconds is < 1 or > 31536000))
                    throw new InvalidOperationException("Choose a positive duration in hours, minutes, and seconds below 24 hours, or leave it blank.");
                foreach (var supplies in new[] { input.Tools, input.Materials })
                    if (supplies.Count > 100 || supplies.Any(x => x.Name?.Length > 300 || x.Quantity?.Length > 300 || x.Url?.Length > 2000 || !PostValidation.IsExternalUrl(x.Url)))
                        throw new InvalidOperationException("Use up to 100 tools or materials, with names and quantities of at most 300 characters and optional absolute HTTP or HTTPS links without credentials.");
            }
            var stepIds = input.Steps.Select(x => x.Id).ToArray();
            if (post.Type == PostType.Guide && await db.GuideSteps.AnyAsync(x => stepIds.Contains(x.Id) && x.PostId != id))
                throw new InvalidOperationException("Steps must belong to this guide.");
            var mediaIds = post.Media.Select(x => x.MediaAssetId).ToHashSet();
            if (input.RemovedMediaIds.Any(x => !mediaIds.Contains(x))) throw new InvalidOperationException("Choose photos belonging to this post.");
            if (input.MediaOrder.Distinct().Count() != input.MediaOrder.Count || input.MediaOrder.Any(x => !mediaIds.Contains(x)) ||
                input.StepImages.Any(x => !mediaIds.Contains(x.Key) || (x.Value is { } step && !stepIds.Contains(step))))
                throw new InvalidOperationException("Choose photos and steps belonging to this post.");
            var names = (input.Tags ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Normalize()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > 20 || names.Any(x => x.Length > 80)) throw new InvalidOperationException("Use up to 20 tags, each at most 80 characters.");
            if (isNew && !HasContent(post.Type, input, names)) return 0;
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (isNew) db.Posts.Add(post);
            var removedMedia = post.Media.Where(x => input.RemovedMediaIds.Contains(x.MediaAssetId)).ToArray();
            db.PostMedia.RemoveRange(removedMedia);
            foreach (var image in removedMedia) post.Media.Remove(image);
            if (input.CoverMediaId is { } removedCover && input.RemovedMediaIds.Contains(removedCover)) input.CoverMediaId = null;
            // Move existing rows out of the unique order ranges before assigning the new order.
            var stepOffset = post.Steps.Count == 0 ? 0 : post.Steps.Max(x => x.Order) + 1;
            foreach (var step in post.Steps) step.Order += stepOffset;
            var mediaOffset = post.Media.Count == 0 ? 0 : post.Media.Max(x => x.SortOrder) + 1;
            foreach (var image in post.Media) image.SortOrder += mediaOffset;
            await db.SaveChangesAsync();
            if (post.Type == PostType.Review)
            {
                post.Review ??= new ReviewDetails();
                post.Review.Subject = input.Subject ?? ""; post.Review.Rating = input.Rating;
                post.Review.ExternalUrl = string.IsNullOrWhiteSpace(input.ExternalUrl) ? null : input.ExternalUrl.Trim();
            }
            if (post.Type == PostType.Guide)
            {
                db.GuideSupplies.RemoveRange(post.Supplies); post.Supplies.Clear();
                foreach (var image in post.Media) image.GuideStepId = null;
                // Clear foreign keys before removing steps; originals remain available for backup.
                await db.SaveChangesAsync();
                var removed = post.Steps.Where(x => !stepIds.Contains(x.Id)).ToArray();
                db.GuideSteps.RemoveRange(removed);
                foreach (var step in removed) post.Steps.Remove(step);
                foreach (var (step, order) in input.Steps.Select((step, order) => (step, order)))
                {
                    var row = post.Steps.SingleOrDefault(x => x.Id == step.Id);
                    if (row is null) { row = new GuideStep { Id = step.Id, PostId = post.Id, Post = post }; db.GuideSteps.Add(row); }
                    row.Order = order; row.Title = string.IsNullOrWhiteSpace(step.Title) ? null : step.Title.Trim();
                    row.BodyMarkdown = step.BodyMarkdown; row.DurationSeconds = (int?)step.DurationSeconds;
                }
                foreach (var (kind, supplies) in new[] { (GuideSupplyKind.Tool, input.Tools), (GuideSupplyKind.Material, input.Materials) })
                    foreach (var (supply, order) in supplies.Select((supply, order) => (supply, order)))
                        db.GuideSupplies.Add(new GuideSupply
                        {
                            PostId = post.Id,
                            Post = post,
                            Kind = kind,
                            Order = order,
                            Name = supply.Name?.Trim() ?? "",
                            Url = string.IsNullOrWhiteSpace(supply.Url) ? null : supply.Url.Trim(),
                            Quantity = kind == GuideSupplyKind.Material && !string.IsNullOrWhiteSpace(supply.Quantity) ? supply.Quantity.Trim() : null
                        });
                foreach (var image in post.Media) image.GuideStepId = input.StepImages.GetValueOrDefault(image.MediaAssetId);
            }
            post.Draft.Title = input.Title; post.Draft.BodyMarkdown = input.BodyMarkdown ?? "";
            post.Draft.Summary = input.Summary; post.Draft.CoverMediaId = input.CoverMediaId;
            post.Draft.LocationName = string.IsNullOrWhiteSpace(input.LocationName) ? null : input.LocationName.Trim();
            post.Draft.LocationAddress = string.IsNullOrWhiteSpace(input.LocationAddress) ? null : input.LocationAddress.Trim();
            post.Draft.Latitude = input.Latitude; post.Draft.Longitude = input.Longitude;
            post.Draft.Version++; post.UpdatedAtUtc = DateTime.UtcNow;
            foreach (var media in post.Media)
            {
                if (input.AltText.TryGetValue(media.MediaAssetId, out var alt)) media.AltText = alt;
                if (input.Captions.TryGetValue(media.MediaAssetId, out var caption)) media.Caption = caption;
            }
            var orderIds = input.MediaOrder.Concat(post.Media.OrderBy(x => x.SortOrder).Select(x => x.MediaAssetId).Where(x => !input.MediaOrder.Contains(x))).ToArray();
            foreach (var image in post.Media) image.SortOrder = Array.IndexOf(orderIds, image.MediaAssetId);
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
            await transaction.CommitAsync();
            return post.Draft.Version;
        }
        finally { writer.Semaphore.Release(); }
    }

    private static bool HasContent(PostType type, ArticleInput input, string[] tags) =>
        !string.IsNullOrWhiteSpace(input.BodyMarkdown) || tags.Length > 0 ||
        !string.IsNullOrWhiteSpace(input.LocationName) || !string.IsNullOrWhiteSpace(input.LocationAddress) ||
        input.Latitude is not null || input.Longitude is not null ||
        (type != PostType.Review && !string.IsNullOrWhiteSpace(input.Title)) ||
        (type == PostType.Article && !string.IsNullOrWhiteSpace(input.Summary)) ||
        (type == PostType.Review && (!string.IsNullOrWhiteSpace(input.Subject) || input.Rating is not null || !string.IsNullOrWhiteSpace(input.ExternalUrl))) ||
        (type == PostType.Guide && (input.Steps.Any(x => !string.IsNullOrWhiteSpace(x.Title) || !string.IsNullOrWhiteSpace(x.BodyMarkdown) || x.DurationSeconds is not null) ||
            input.Tools.Concat(input.Materials).Any(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Url)) ||
            input.Materials.Any(x => !string.IsNullOrWhiteSpace(x.Quantity))));

    public static ArticleSnapshot Snapshot(Post post, string slug, DateTime now) => new(1, post.Id, slug,
        post.Draft!.Title ?? "", post.Draft.BodyMarkdown, post.Draft.Summary,
        post.FirstPublishedAtUtc ?? now, now, post.Draft.Version,
        post.Tags.Select(x => x.Tag.Name).ToArray(), post.Draft.CoverMediaId,
        DraftMedia(post).OrderBy(x => x.SortOrder).Select(x => new ArticleMedia(x.MediaAssetId,
            x.MediaAsset.StoredFilename, x.MediaAsset.MimeType, x.AltText ?? "")
        { Caption = x.Caption, GuideStepId = x.GuideStepId }).ToArray())
    {
        Type = post.Type,
        Location = LocationSnapshot.From(post.Draft),
        Activity = post.Activity is { } activity ? JsonSerializer.Deserialize<FitnessSnapshot>(activity.TrackJson) : null,
        TagSlugs = post.Tags.ToDictionary(x => x.Tag.Name, x => x.Tag.Slug),
        Review = post.Review is { } review ? new(review.Subject, review.Rating, review.ExternalUrl) : null,
        Steps = post.Steps.OrderBy(x => x.Order).Select(GuideStepSnapshot.From).ToArray(),
        Tools = SupplySnapshots(post, GuideSupplyKind.Tool),
        Materials = SupplySnapshots(post, GuideSupplyKind.Material)
    };

    private static GuideSupplySnapshot[] SupplySnapshots(Post post, GuideSupplyKind kind) => ArticleInput.GuideSupplies(post, kind)
        .Select(x => new GuideSupplySnapshot(x.Name, x.Url) { Quantity = x.Quantity }).ToArray();

    // Pasted images belong to Markdown, not the implicit photo grid. Keep the private
    // attachment for undo/reinsertion and backup, but omit unused uploads from all snapshots.
    public static IEnumerable<PostMedia> DraftMedia(Post post)
    {
        var urls = SafeMarkdown.ImageUrls(new[] { post.Draft!.BodyMarkdown }.Concat(post.Steps.Select(x => x.BodyMarkdown)));
        return post.Media.Where(x => !x.InlineOnly || x.MediaAssetId == post.Draft.CoverMediaId ||
            urls.Contains($"/media/{x.MediaAssetId:D}/{x.MediaAsset.StoredFilename}"));
    }

    public static bool HasDraftChanges(Post post, ArticleSnapshot live) => post.Draft is { } draft &&
        ((draft.Title ?? "") != live.Title || draft.BodyMarkdown != live.BodyMarkdown || draft.Summary != live.Summary ||
         draft.CoverMediaId != live.CoverMediaId ||
         LocationSnapshot.From(draft) != live.Location ||
         (post.Type == PostType.Fitness && (post.Activity?.TrackJson ?? "null") != JsonSerializer.Serialize(live.Activity)) ||
         !post.Tags.Select(x => x.Tag.Name).Order(StringComparer.Ordinal).SequenceEqual(live.Tags.Order(StringComparer.Ordinal)) ||
         live.Media.Any(x => post.Media.SingleOrDefault(m => m.MediaAssetId == x.Id) is not { } media ||
             (media.AltText ?? "") != x.AltText || media.Caption != x.Caption || media.GuideStepId != x.GuideStepId) ||
         (post.Type is PostType.Microblog or PostType.Review or PostType.Gallery or PostType.Guide or PostType.Checkin or PostType.Fitness &&
          !DraftMedia(post).OrderBy(x => x.SortOrder).Select(x => x.MediaAssetId).SequenceEqual(live.Media.Select(x => x.Id))) ||
         (post.Type == PostType.Review && new ReviewSnapshot(post.Review?.Subject ?? "", post.Review?.Rating, post.Review?.ExternalUrl) != live.Review) ||
         (post.Type == PostType.Guide && (!post.Steps.OrderBy(x => x.Order).Select(GuideStepSnapshot.From).SequenceEqual(live.Steps) ||
             !SupplySnapshots(post, GuideSupplyKind.Tool).SequenceEqual(live.Tools) || !SupplySnapshots(post, GuideSupplyKind.Material).SequenceEqual(live.Materials))));

    public static bool IsPublicationPending(Post post, ArticleSnapshot? live)
    {
        if (live is null) return post.Status == PostStatus.Published;
        if (post.Status != PostStatus.Published || post.PublishedSnapshot is null) return true;
        var desired = JsonSerializer.Deserialize<ArticleSnapshot>(post.PublishedSnapshot.SnapshotJson);
        // Legacy snapshots acquire tag URLs during generation, without pulling in draft edits.
        if (desired is { TagSlugs.Count: 0 }) desired = desired with { TagSlugs = live.TagSlugs };
        return JsonSerializer.Serialize(desired) != JsonSerializer.Serialize(live);
    }
}
