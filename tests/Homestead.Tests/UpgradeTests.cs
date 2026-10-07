using Homestead.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Homestead.Tests;

public sealed class UpgradeTests
{
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
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
