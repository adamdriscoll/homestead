using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Homestead.Data.Migrations;

/// <inheritdoc />
public partial class AutomatedBackups : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BackupSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                Frequency = table.Column<int>(type: "INTEGER", nullable: false),
                Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                Region = table.Column<string>(type: "TEXT", nullable: false),
                Bucket = table.Column<string>(type: "TEXT", nullable: false),
                Prefix = table.Column<string>(type: "TEXT", nullable: false),
                ForcePathStyle = table.Column<bool>(type: "INTEGER", nullable: false),
                ProtectedAccessKey = table.Column<string>(type: "TEXT", nullable: false),
                ProtectedSecretKey = table.Column<string>(type: "TEXT", nullable: false),
                LastAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastSuccessAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastObjectKey = table.Column<string>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BackupSettings", x => x.Id);
                table.CheckConstraint("CK_BackupSettings_Frequency", "Frequency BETWEEN 0 AND 2");
                table.CheckConstraint("CK_BackupSettings_Id", "Id = 1");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BackupSettings");
    }
}
