using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class Newsletter : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "EnableNewsletter",
            table: "SiteSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "NewsletterPosts",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActivatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NewsletterPosts", x => x.PostId);
            });

        migrationBuilder.CreateTable(
            name: "NewsletterSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                ProtectedApiKey = table.Column<string>(type: "TEXT", nullable: false),
                SenderEmail = table.Column<string>(type: "TEXT", nullable: false),
                SenderName = table.Column<string>(type: "TEXT", nullable: false),
                EnabledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastSuccessAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NewsletterSettings", x => x.Id);
                table.CheckConstraint("CK_NewsletterSettings_Id", "Id = 1");
            });

        migrationBuilder.CreateTable(
            name: "NewsletterSubscribers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false, collation: "NOCASE"),
                Subscribed = table.Column<bool>(type: "INTEGER", nullable: false),
                Frequency = table.Column<int>(type: "INTEGER", nullable: false),
                PostTypes = table.Column<int>(type: "INTEGER", nullable: false),
                ConfirmedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                NextDigestAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                TokenVersion = table.Column<Guid>(type: "TEXT", nullable: false),
                PendingToken = table.Column<Guid>(type: "TEXT", nullable: false),
                PendingFrequency = table.Column<int>(type: "INTEGER", nullable: false),
                PendingPostTypes = table.Column<int>(type: "INTEGER", nullable: false),
                ConfirmationExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastConfirmationAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NewsletterSubscribers", x => x.Id);
                table.CheckConstraint("CK_NewsletterSubscriber_Frequency", "Frequency BETWEEN 0 AND 1 AND PendingFrequency BETWEEN 0 AND 1");
                table.CheckConstraint("CK_NewsletterSubscriber_Types", "PostTypes BETWEEN 1 AND 63 AND PendingPostTypes BETWEEN 0 AND 63");
            });

        migrationBuilder.CreateTable(
            name: "NewsletterDeliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                SubscriberId = table.Column<Guid>(type: "TEXT", nullable: false),
                Key = table.Column<string>(type: "TEXT", nullable: false),
                PostIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                Subject = table.Column<string>(type: "TEXT", nullable: true),
                Html = table.Column<string>(type: "TEXT", nullable: true),
                Text = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NewsletterDeliveries", x => x.Id);
                table.CheckConstraint("CK_NewsletterDelivery_Status", "Status BETWEEN 0 AND 2");
                table.ForeignKey(
                    name: "FK_NewsletterDeliveries_NewsletterSubscribers_SubscriberId",
                    column: x => x.SubscriberId,
                    principalTable: "NewsletterSubscribers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NewsletterDeliveries_Status_NextAttemptAtUtc",
            table: "NewsletterDeliveries",
            columns: new[] { "Status", "NextAttemptAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_NewsletterDeliveries_SubscriberId_Key",
            table: "NewsletterDeliveries",
            columns: new[] { "SubscriberId", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_NewsletterSubscribers_Email",
            table: "NewsletterSubscribers",
            column: "Email",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "NewsletterDeliveries");

        migrationBuilder.DropTable(
            name: "NewsletterPosts");

        migrationBuilder.DropTable(
            name: "NewsletterSettings");

        migrationBuilder.DropTable(
            name: "NewsletterSubscribers");

        migrationBuilder.DropColumn(
            name: "EnableNewsletter",
            table: "SiteSettings");
    }
}
