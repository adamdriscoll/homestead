using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class FitnessDisplayUnits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FitnessUnits",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Settings_FitnessUnits",
            table: "SiteSettings",
            sql: "FitnessUnits BETWEEN 0 AND 1");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Settings_FitnessUnits",
            table: "SiteSettings");

        migrationBuilder.DropColumn(
            name: "FitnessUnits",
            table: "SiteSettings");
    }
}
