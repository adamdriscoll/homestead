using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class OptimizedPostImages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RetainedOriginalFilename",
            table: "MediaAssets",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "RetainedOriginalSize",
            table: "MediaAssets",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RetainedOriginalFilename",
            table: "MediaAssets");

        migrationBuilder.DropColumn(
            name: "RetainedOriginalSize",
            table: "MediaAssets");
    }
}
