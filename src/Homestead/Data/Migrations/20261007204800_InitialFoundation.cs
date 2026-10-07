using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class InitialFoundation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                AdminSlot = table.Column<int>(type: "INTEGER", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                table.CheckConstraint("CK_User_AdminSlot", "AdminSlot = 1");
            });

        migrationBuilder.CreateTable(
            name: "MediaAssets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                StoredFilename = table.Column<string>(type: "TEXT", nullable: false),
                OriginalFilename = table.Column<string>(type: "TEXT", nullable: false),
                MimeType = table.Column<string>(type: "TEXT", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                Width = table.Column<int>(type: "INTEGER", nullable: false),
                Height = table.Column<int>(type: "INTEGER", nullable: false),
                UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaAssets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Posts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Slug = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true, collation: "NOCASE"),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                FirstPublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastPublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Posts", x => x.Id);
                table.CheckConstraint("CK_Post_PublishedSlug", "FirstPublishedAtUtc IS NULL OR (Slug IS NOT NULL AND length(trim(Slug)) > 0)");
                table.CheckConstraint("CK_Post_Status", "Status BETWEEN 0 AND 3");
                table.CheckConstraint("CK_Post_Type", "Type BETWEEN 0 AND 4");
            });

        migrationBuilder.CreateTable(
            name: "SiteGenerations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                BuiltAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ActivatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                OutputRoot = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Error = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SiteGenerations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Tags",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                NormalizedName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                Slug = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tags", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                UserId = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SiteSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                CanonicalBaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                DisplayAuthor = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                TimeZone = table.Column<string>(type: "TEXT", nullable: false),
                AboutMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                AuthorBio = table.Column<string>(type: "TEXT", nullable: false),
                FooterText = table.Column<string>(type: "TEXT", nullable: false),
                AccentColor = table.Column<string>(type: "TEXT", nullable: false),
                AvatarMediaId = table.Column<Guid>(type: "TEXT", nullable: true),
                LogoMediaId = table.Column<Guid>(type: "TEXT", nullable: true),
                HeaderMediaId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SiteSettings", x => x.Id);
                table.CheckConstraint("CK_Settings_Singleton", "Id = 1");
                table.ForeignKey(
                    name: "FK_SiteSettings_MediaAssets_AvatarMediaId",
                    column: x => x.AvatarMediaId,
                    principalTable: "MediaAssets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_SiteSettings_MediaAssets_HeaderMediaId",
                    column: x => x.HeaderMediaId,
                    principalTable: "MediaAssets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_SiteSettings_MediaAssets_LogoMediaId",
                    column: x => x.LogoMediaId,
                    principalTable: "MediaAssets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "GuideSteps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                Order = table.Column<int>(type: "INTEGER", nullable: false),
                BodyMarkdown = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GuideSteps", x => x.Id);
                table.UniqueConstraint("AK_GuideSteps_Id_PostId", x => new { x.Id, x.PostId });
                table.CheckConstraint("CK_Step_Order", "\"Order\" >= 0");
                table.ForeignKey(
                    name: "FK_GuideSteps_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PostDrafts",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: true),
                BodyMarkdown = table.Column<string>(type: "TEXT", nullable: false),
                Summary = table.Column<string>(type: "TEXT", nullable: true),
                Version = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PostDrafts", x => x.PostId);
                table.ForeignKey(
                    name: "FK_PostDrafts_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PublishedSnapshots",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                PublishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublishedSnapshots", x => x.PostId);
                table.ForeignKey(
                    name: "FK_PublishedSnapshots_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Reviews",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                Subject = table.Column<string>(type: "TEXT", nullable: false),
                Rating = table.Column<int>(type: "INTEGER", nullable: true),
                ExternalUrl = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Reviews", x => x.PostId);
                table.CheckConstraint("CK_Review_Rating", "Rating IS NULL OR Rating BETWEEN 1 AND 5");
                table.ForeignKey(
                    name: "FK_Reviews_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PostTags",
            columns: table => new
            {
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PostTags", x => new { x.PostId, x.TagId });
                table.ForeignKey(
                    name: "FK_PostTags_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PostTags_Tags_TagId",
                    column: x => x.TagId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PostMedia",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PostId = table.Column<Guid>(type: "TEXT", nullable: false),
                MediaAssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<int>(type: "INTEGER", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                Caption = table.Column<string>(type: "TEXT", nullable: true),
                AltText = table.Column<string>(type: "TEXT", nullable: true),
                GuideStepId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PostMedia", x => x.Id);
                table.CheckConstraint("CK_Media_Order", "SortOrder >= 0");
                table.CheckConstraint("CK_Media_Role", "Role BETWEEN 0 AND 3");
                table.ForeignKey(
                    name: "FK_PostMedia_GuideSteps_GuideStepId_PostId",
                    columns: x => new { x.GuideStepId, x.PostId },
                    principalTable: "GuideSteps",
                    principalColumns: new[] { "Id", "PostId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PostMedia_MediaAssets_MediaAssetId",
                    column: x => x.MediaAssetId,
                    principalTable: "MediaAssets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_PostMedia_Posts_PostId",
                    column: x => x.PostId,
                    principalTable: "Posts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserClaims_UserId",
            table: "AspNetUserClaims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserLogins_UserId",
            table: "AspNetUserLogins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "NormalizedEmail");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUsers_AdminSlot",
            table: "AspNetUsers",
            column: "AdminSlot",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "NormalizedUserName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_GuideSteps_PostId_Order",
            table: "GuideSteps",
            columns: new[] { "PostId", "Order" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_StoredFilename",
            table: "MediaAssets",
            column: "StoredFilename",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PostMedia_GuideStepId_PostId",
            table: "PostMedia",
            columns: new[] { "GuideStepId", "PostId" });

        migrationBuilder.CreateIndex(
            name: "IX_PostMedia_MediaAssetId",
            table: "PostMedia",
            column: "MediaAssetId");

        migrationBuilder.CreateIndex(
            name: "IX_PostMedia_PostId_Role_SortOrder",
            table: "PostMedia",
            columns: new[] { "PostId", "Role", "SortOrder" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Posts_Slug",
            table: "Posts",
            column: "Slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PostTags_TagId",
            table: "PostTags",
            column: "TagId");

        migrationBuilder.CreateIndex(
            name: "IX_SiteSettings_AvatarMediaId",
            table: "SiteSettings",
            column: "AvatarMediaId");

        migrationBuilder.CreateIndex(
            name: "IX_SiteSettings_HeaderMediaId",
            table: "SiteSettings",
            column: "HeaderMediaId");

        migrationBuilder.CreateIndex(
            name: "IX_SiteSettings_LogoMediaId",
            table: "SiteSettings",
            column: "LogoMediaId");

        migrationBuilder.CreateIndex(
            name: "IX_Tags_NormalizedName",
            table: "Tags",
            column: "NormalizedName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Tags_Slug",
            table: "Tags",
            column: "Slug",
            unique: true);

        migrationBuilder.Sql("""
            CREATE TRIGGER TR_Post_LockedPublication
            BEFORE UPDATE ON Posts
            WHEN OLD.FirstPublishedAtUtc IS NOT NULL
              AND (NEW.Slug IS NOT OLD.Slug OR NEW.FirstPublishedAtUtc IS NOT OLD.FirstPublishedAtUtc)
            BEGIN
              SELECT RAISE(ABORT, 'The first publication slug and timestamp are immutable.');
            END;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_Post_LockedPublication;");
        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "PostDrafts");

        migrationBuilder.DropTable(
            name: "PostMedia");

        migrationBuilder.DropTable(
            name: "PostTags");

        migrationBuilder.DropTable(
            name: "PublishedSnapshots");

        migrationBuilder.DropTable(
            name: "Reviews");

        migrationBuilder.DropTable(
            name: "SiteGenerations");

        migrationBuilder.DropTable(
            name: "SiteSettings");

        migrationBuilder.DropTable(
            name: "AspNetUsers");

        migrationBuilder.DropTable(
            name: "GuideSteps");

        migrationBuilder.DropTable(
            name: "Tags");

        migrationBuilder.DropTable(
            name: "MediaAssets");

        migrationBuilder.DropTable(
            name: "Posts");
    }
}
