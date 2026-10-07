using Homestead.Data;
using Homestead.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Homestead.Tests;

public sealed class DomainTests
{
    private static async Task<HomesteadDbContext> Database(TemporaryData data)
    {
        Directory.CreateDirectory(data.Path);
        var db = new HomesteadDbContext(new DbContextOptionsBuilder<HomesteadDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(data.Path, "domain.db"), ForeignKeys = true }.ToString()).Options);
        await db.Database.MigrateAsync(); return db;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Ratings_are_checked_in_the_database(int rating)
    {
        using var data = new TemporaryData(); await using var db = await Database(data);
        db.Posts.Add(new Post { Type = PostType.Review, Review = new ReviewDetails { Rating = rating } });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Incomplete_and_valid_review_drafts_can_be_saved(int? rating)
    {
        using var data = new TemporaryData(); await using var db = await Database(data);
        db.Posts.Add(new Post { Type = PostType.Review, Review = new ReviewDetails { Rating = rating } });
        await db.SaveChangesAsync(); Assert.Equal(rating, (await db.Reviews.SingleAsync()).Rating);
    }

    [Fact]
    public async Task Slugs_are_unique_case_insensitively_while_incomplete_drafts_have_no_slug()
    {
        using var data = new TemporaryData(); await using var db = await Database(data);
        db.Posts.AddRange(new Post(), new Post(), new Post { Slug = "story" }); await db.SaveChangesAsync();
        db.Posts.Add(new Post { Slug = "STORY" }); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Tag_names_and_slugs_have_normalized_uniqueness()
    {
        using var data = new TemporaryData(); await using var db = await Database(data);
        db.Tags.Add(new Tag { Name = " Hiking ", Slug = "HIKING" }); await db.SaveChangesAsync();
        Assert.Equal("HIKING", (await db.Tags.SingleAsync()).NormalizedName);
        db.Tags.Add(new Tag { Name = "hiking", Slug = "other" }); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); db.Tags.Add(new Tag { Name = "other", Slug = "hiking" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task First_publication_locks_the_slug_and_timestamp_even_for_direct_SQL()
    {
        using var data = new TemporaryData(); await using var db = await Database(data);
        var post = new Post { Slug = "original", FirstPublishedAtUtc = DateTime.UtcNow }; db.Posts.Add(post); await db.SaveChangesAsync();
        post.Status = PostStatus.Unpublished; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Posts SET Slug = 'changed' WHERE Id = {post.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Posts SET FirstPublishedAtUtc = NULL WHERE Id = {post.Id}"));
        db.ChangeTracker.Clear(); Assert.Equal("original", (await db.Posts.SingleAsync()).Slug);
    }

    [Fact]
    public async Task Draft_edits_do_not_mutate_the_snapshot_and_dates_round_trip_as_UTC()
    {
        using var data = new TemporaryData();
        var time = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Local);
        await using (var db = await Database(data))
        {
            db.Posts.Add(new Post
            {
                CreatedAtUtc = time,
                Draft = new PostDraft { BodyMarkdown = "original" },
                PublishedSnapshot = new PostPublishedSnapshot { SnapshotJson = "{\"body\":\"public\"}", PublishedAtUtc = time }
            });
            await db.SaveChangesAsync(); (await db.PostDrafts.SingleAsync()).BodyMarkdown = "secret edited draft"; await db.SaveChangesAsync();
        }
        await using var restarted = await Database(data);
        Assert.Equal("{\"body\":\"public\"}", (await restarted.PublishedSnapshots.SingleAsync()).SnapshotJson);
        Assert.Equal(time.ToUniversalTime(), (await restarted.Posts.SingleAsync()).CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, (await restarted.Posts.SingleAsync()).CreatedAtUtc.Kind);
        Assert.Empty(await restarted.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Relationships_enforce_media_ownership_foreign_keys_and_step_order()
    {
        using var data = new TemporaryData(); await using var db = await Database(data);
        var guide = new Post { Type = PostType.Guide }; var other = new Post { Type = PostType.Guide };
        var step = new GuideStep { Post = guide, Order = 0 }; var media = new MediaAsset { StoredFilename = "safe.jpg" };
        db.Posts.AddRange(guide, other); db.GuideSteps.Add(step); db.MediaAssets.Add(media); await db.SaveChangesAsync();
        db.PostMedia.Add(new PostMedia { PostId = other.Id, MediaAssetId = media.Id, GuideStepId = step.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); db.PostDrafts.Add(new PostDraft { PostId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); db.GuideSteps.Add(new GuideStep { PostId = guide.Id, Order = 0 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); db.PostTags.Add(new PostTag { PostId = guide.Id, TagId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
