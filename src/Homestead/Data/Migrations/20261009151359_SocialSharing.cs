using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class SocialSharing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ShareBluesky",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ShareEmail",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ShareFacebook",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ShareLinkedIn",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ShareReddit",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ShareX",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShareBluesky",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ShareEmail",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ShareFacebook",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ShareLinkedIn",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ShareReddit",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "ShareX",
            table: "SiteSettings");
    }
}
