using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class GuideStepTitles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Title",
            table: "GuideSteps",
            type: "TEXT",
            maxLength: 300,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Title",
            table: "GuideSteps");
    }
}
