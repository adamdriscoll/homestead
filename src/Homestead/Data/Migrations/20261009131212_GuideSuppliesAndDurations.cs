using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class GuideSuppliesAndDurations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DurationSeconds",
            table: "GuideSteps",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "GuideSupplies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<int>(type: "INTEGER", nullable: false),
                Order = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GuideSupplies", x => x.Id);
                table.CheckConstraint("CK_Supply_Kind", "Kind BETWEEN 0 AND 1");
                table.CheckConstraint("CK_Supply_Order", "\"Order\" >= 0");
                table.ForeignKey(
                    name: "FK_GuideSupplies_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Step_Duration",
            table: "GuideSteps",
            sql: "DurationSeconds IS NULL OR DurationSeconds BETWEEN 1 AND 31536000");

        migrationBuilder.CreateIndex(
            name: "IX_GuideSupplies_PostId_Kind_Order",
            table: "GuideSupplies",
            columns: new[] { "PostId", "Kind", "Order" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GuideSupplies");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Step_Duration",
            table: "GuideSteps");

        migrationBuilder.DropColumn(
            name: "DurationSeconds",
            table: "GuideSteps");
    }
}
