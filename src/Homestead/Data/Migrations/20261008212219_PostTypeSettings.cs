using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class PostTypeSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "EnableArticles",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EnableCheckins",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EnableGalleries",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EnableGuides",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EnableNotes",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "EnableReviews",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EnableArticles",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "EnableCheckins",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "EnableGalleries",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "EnableGuides",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "EnableNotes",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "EnableReviews",
            table: "SiteSettings");
    }
}
