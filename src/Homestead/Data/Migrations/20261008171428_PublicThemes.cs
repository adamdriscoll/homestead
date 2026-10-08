using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class PublicThemes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ThemeBackgroundColor",
            table: "SiteSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ThemeId",
            table: "SiteSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "porch");

        migrationBuilder.AddColumn<string>(
            name: "ThemePrimaryColor",
            table: "SiteSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ThemeTextColor",
            table: "SiteSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ThemeBackgroundColor",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ThemeId",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ThemePrimaryColor",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ThemeTextColor",
            table: "SiteSettings");
    }
}
