using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class LocationMap : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "EnableLocationMap",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EnableLocationMap",
            table: "SiteSettings");
    }
}
