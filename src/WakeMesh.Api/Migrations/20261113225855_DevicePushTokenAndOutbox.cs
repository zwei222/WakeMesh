using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WakeMesh.Api.Migrations;

/// <inheritdoc />
public partial class _20261113225855_DevicePushTokenAndOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PushToken",
            table: "Devices",
            type: "nvarchar(4096)",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PushTokenUpdatedAt",
            table: "Devices",
            type: "datetimeoffset",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "PushOutbox",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DesiredRevision = table.Column<long>(type: "bigint", nullable: false),
                MessageType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PushOutbox", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PushOutbox_DeviceId_DesiredRevision_Status",
            table: "PushOutbox",
            columns: new[] { "DeviceId", "DesiredRevision", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_PushOutbox_Status_NextAttemptAt",
            table: "PushOutbox",
            columns: new[] { "Status", "NextAttemptAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PushOutbox");

        migrationBuilder.DropColumn(
            name: "PushToken",
            table: "Devices");

        migrationBuilder.DropColumn(
            name: "PushTokenUpdatedAt",
            table: "Devices");
    }
}
