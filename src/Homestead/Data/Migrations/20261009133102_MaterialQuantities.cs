using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;
/// <inheritdoc />
public partial class MaterialQuantities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Quantity",
            table: "GuideSupplies",
            type: "TEXT",
            maxLength: 300,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Quantity",
            table: "GuideSupplies");
    }
}
