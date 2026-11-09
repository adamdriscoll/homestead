using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class BasicAnalytics : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnalyticsSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AnalyticsSettings", x => x.Id);
                table.CheckConstraint("CK_AnalyticsSettings_Id", "Id = 1");
            });

        migrationBuilder.CreateTable(
            name: "PageViews",
            columns: table => new
            {
                Path = table.Column<string>(type: "TEXT", nullable: false),
                Hits = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PageViews", x => x.Path);
                table.CheckConstraint("CK_PageViews_Hits", "Hits >= 0");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AnalyticsSettings");

        migrationBuilder.DropTable(
            name: "PageViews");
    }
}
