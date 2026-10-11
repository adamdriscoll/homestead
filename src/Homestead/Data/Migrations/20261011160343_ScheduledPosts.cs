using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class ScheduledPosts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PostSchedules",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                PublishAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                Committed = table.Column<bool>(type: "INTEGER", nullable: false),
                LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Error = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PostSchedules", x => x.PostId);
                table.ForeignKey(
                    name: "FK_PostSchedules_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PostSchedules_NextAttemptAtUtc",
            table: "PostSchedules",
            column: "NextAttemptAtUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PostSchedules");
    }
}
