using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class PublicFavicon : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "FaviconMediaId",
            table: "SiteSettings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SiteSettings_FaviconMediaId",
            table: "SiteSettings",
            column: "FaviconMediaId");

        migrationBuilder.AddForeignKey(
            name: "FK_SiteSettings_MediaAssets_FaviconMediaId",
            table: "SiteSettings",
            column: "FaviconMediaId",
            principalTable: "MediaAssets",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_SiteSettings_MediaAssets_FaviconMediaId",
            table: "SiteSettings");

        migrationBuilder.DropIndex(
            name: "IX_SiteSettings_FaviconMediaId",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "FaviconMediaId",
            table: "SiteSettings");
    }
}
