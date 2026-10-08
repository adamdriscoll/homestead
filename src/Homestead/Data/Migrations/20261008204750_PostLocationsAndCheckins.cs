using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class PostLocationsAndCheckins : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts");

        migrationBuilder.AddColumn<double>(
            name: "Latitude",
            table: "PostDrafts",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LocationAddress",
            table: "PostDrafts",
            type: "TEXT",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LocationName",
            table: "PostDrafts",
            type: "TEXT",
            maxLength: 300,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "Longitude",
            table: "PostDrafts",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts",
            sql: "Type BETWEEN 0 AND 5");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Draft_Coordinates",
            table: "PostDrafts",
            sql: "(Latitude IS NULL AND Longitude IS NULL) OR (Latitude IS NOT NULL AND Longitude IS NOT NULL AND Latitude BETWEEN -90 AND 90 AND Longitude BETWEEN -180 AND 180)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Draft_Coordinates",
            table: "PostDrafts");

        migrationBuilder.DropColumn(
            name: "Latitude",
            table: "PostDrafts");

        migrationBuilder.DropColumn(
            name: "LocationAddress",
            table: "PostDrafts");

        migrationBuilder.DropColumn(
            name: "LocationName",
            table: "PostDrafts");

        migrationBuilder.DropColumn(
            name: "Longitude",
            table: "PostDrafts");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts",
            sql: "Type BETWEEN 0 AND 4");
    }
}
