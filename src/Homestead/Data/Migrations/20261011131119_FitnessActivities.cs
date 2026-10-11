using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class FitnessActivities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts");

        migrationBuilder.DropCheckConstraint(
            name: "CK_NewsletterSubscriber_Types",
            table: "NewsletterSubscribers");

        migrationBuilder.AddColumn<bool>(
            name: "EnableFitness",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateTable(
            name: "FitnessActivities",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                OriginalGpx = table.Column<byte[]>(type: "BLOB", nullable: false),
                TrackJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FitnessActivities", x => x.PostId);
                table.ForeignKey(
                    name: "FK_FitnessActivities_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts",
            sql: "Type BETWEEN 0 AND 6");

        migrationBuilder.AddCheckConstraint(
            name: "CK_NewsletterSubscriber_Types",
            table: "NewsletterSubscribers",
            sql: "PostTypes BETWEEN 1 AND 127 AND PendingPostTypes BETWEEN 0 AND 127");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "FitnessActivities");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts");

        migrationBuilder.DropCheckConstraint(
            name: "CK_NewsletterSubscriber_Types",
            table: "NewsletterSubscribers");

        migrationBuilder.DropColumn(
            name: "EnableFitness",
            table: "SiteSettings");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Post_Type",
            table: "Posts",
            sql: "Type BETWEEN 0 AND 5");

        migrationBuilder.AddCheckConstraint(
            name: "CK_NewsletterSubscriber_Types",
            table: "NewsletterSubscribers",
            sql: "PostTypes BETWEEN 1 AND 63 AND PendingPostTypes BETWEEN 0 AND 63");
    }
}
