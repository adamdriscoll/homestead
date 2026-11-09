using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class AboutFeaturedImage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AboutImageAltText",
            table: "SiteSettings",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<Guid>(
            name: "AboutImageMediaId",
            table: "SiteSettings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SiteSettings_AboutImageMediaId",
            table: "SiteSettings",
            column: "AboutImageMediaId");

        migrationBuilder.AddForeignKey(
            name: "FK_SiteSettings_MediaAssets_AboutImageMediaId",
            table: "SiteSettings",
            column: "AboutImageMediaId",
            principalTable: "MediaAssets",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_SiteSettings_MediaAssets_AboutImageMediaId",
            table: "SiteSettings");

        migrationBuilder.DropIndex(
            name: "IX_SiteSettings_AboutImageMediaId",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "AboutImageAltText",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "AboutImageMediaId",
            table: "SiteSettings");
    }
}
