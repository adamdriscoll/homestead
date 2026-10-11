using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class RestoreFitnessPublicationTrigger : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // SQLite rebuilds Posts at the end of FitnessActivities. Restore the trigger afterwards.
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_Post_LockedPublication
            BEFORE UPDATE ON Posts
            WHEN OLD.FirstPublishedAtUtc IS NOT NULL
              AND (NEW.Slug IS NOT OLD.Slug OR NEW.FirstPublishedAtUtc IS NOT OLD.FirstPublishedAtUtc)
            BEGIN
              SELECT RAISE(ABORT, 'The first publication slug and timestamp are immutable.');
            END;
            """);

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_Post_LockedPublication;");

    }
}
