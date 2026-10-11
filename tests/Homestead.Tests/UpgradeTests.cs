using Homestead.Data;
using Homestead.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Homestead.Tests;

public sealed class UpgradeTests
{
    [Fact]
    public async Task Fitness_upgrade_preserves_subscriber_choices_posts_and_publication_trigger()
    {
        using var data = new TemporaryData(); Directory.CreateDirectory(data.Path);
        await using var db = new HomesteadDbContext(new DbContextOptionsBuilder<HomesteadDbContext>()
            .UseSqlite($"Data Source={Path.Combine(data.Path, "fitness-upgrade.db")}").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261009175208_Newsletter");
        var time = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var old = new Post { Type = PostType.Article, Slug = "unchanged", FirstPublishedAtUtc = time, Draft = new() { BodyMarkdown = "Private draft", Version = 4 } };
        db.Posts.Add(old);
        db.NewsletterSubscribers.Add(new() { Email = "existing@example.test", PostTypes = 63, PendingPostTypes = 63 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Equal(63, (await db.NewsletterSubscribers.SingleAsync()).PostTypes);
        Assert.Equal(63, (await db.NewsletterSubscribers.SingleAsync()).PendingPostTypes);
        var post = await db.Posts.Include(x => x.Draft).SingleAsync();
        Assert.Equal(time, post.FirstPublishedAtUtc); Assert.Equal(4, post.Draft!.Version); Assert.Equal("Private draft", post.Draft.BodyMarkdown);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Posts SET Slug = 'changed' WHERE Id = {old.Id}"));
        db.Posts.Add(new() { Type = PostType.Fitness, Draft = new() });
        (await db.NewsletterSubscribers.SingleAsync()).PostTypes = 127;
        await db.SaveChangesAsync(); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Location_upgrade_preserves_posts_and_recreates_the_publication_trigger()
    {
        using var data = new TemporaryData(); Directory.CreateDirectory(data.Path);
        await using var db = new HomesteadDbContext(new DbContextOptionsBuilder<HomesteadDbContext>()
            .UseSqlite($"Data Source={Path.Combine(data.Path, "upgrade.db")}").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261008193009_HomesteadCredit");
        var id = Guid.NewGuid(); var time = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Posts (Id, Type, Status, Slug, CreatedAtUtc, UpdatedAtUtc, FirstPublishedAtUtc) VALUES ({id}, 1, 1, 'original', {time}, {time}, {time})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO PostDrafts (PostId, Title, BodyMarkdown, Version) VALUES ({id}, 'Original', 'Private draft', 7)");
        await db.Database.MigrateAsync();
        var post = await db.Posts.Include(x => x.Draft).SingleAsync();
        Assert.Equal("original", post.Slug); Assert.Equal(time, post.FirstPublishedAtUtc);
        Assert.Equal("Private draft", post.Draft!.BodyMarkdown); Assert.Equal(7, post.Draft.Version);
        Assert.Null(post.Draft.LocationName); Assert.Null(post.Draft.Latitude);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Posts SET Slug = 'changed' WHERE Id = {id}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Posts SET FirstPublishedAtUtc = NULL WHERE Id = {id}"));
        db.Posts.Add(new Post { Type = PostType.Checkin, Draft = new() { LocationName = "New place" } });
        await db.SaveChangesAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Phase_two_database_upgrade_preserves_settings_and_initializes_profile_links()
    {
        using var data = new TemporaryData(); Directory.CreateDirectory(data.Path);
        await using var db = new HomesteadDbContext(new DbContextOptionsBuilder<HomesteadDbContext>()
            .UseSqlite($"Data Source={Path.Combine(data.Path, "upgrade.db")}").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261007212753_ArticlePublishing");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO SiteSettings (Id, Title, CanonicalBaseUrl, DisplayAuthor, Description, TimeZone,
                AboutMarkdown, AuthorBio, FooterText, AccentColor)
            VALUES (1, 'Existing home', 'https://example.test', 'Ada', 'Original description', 'UTC',
                '# Original About', 'An author', 'My footer', 'Plum')
            """);
        await db.Database.MigrateAsync();
        var settings = await db.SiteSettings.SingleAsync();
        Assert.Equal("Existing home", settings.Title); Assert.Equal("# Original About", settings.AboutMarkdown);
        Assert.Equal("[]", settings.ProfileLinksJson); Assert.Empty(settings.ProfileLinks);
        Assert.False(settings.HideHomesteadCredit);
        Assert.Empty(Homestead.Features.Publishing.SocialSharing.Templates(settings));
        Assert.Equal(Enum.GetValues<PostType>(), settings.EnabledPostTypes);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
