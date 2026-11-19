using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WakeMesh.Api.Migrations;

/// <inheritdoc />
public partial class _20261113192122_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AckRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                Result = table.Column<string>(type: "nvarchar(max)", nullable: false),
                FailuresJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AckRecords", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AlarmGroups",
            columns: table => new
            {
                Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Enabled = table.Column<bool>(type: "bit", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                Repeat = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                DaysOrDateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ExcludeHolidays = table.Column<bool>(type: "bit", nullable: false),
                ExcludedDatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AlarmGroups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                CredentialHash = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                DesiredRevision = table.Column<long>(type: "bigint", nullable: false),
                AppliedRevision = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Devices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SnapshotReceipts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ConsumedSuccess = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SnapshotReceipts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Tombstones",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedAtRevision = table.Column<long>(type: "bigint", nullable: false),
                Cleared = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tombstones", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "DeviceSchedules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                GroupId = table.Column<string>(type: "nvarchar(64)", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Time = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                SoundKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                Volume = table.Column<int>(type: "int", nullable: false),
                Vibrate = table.Column<bool>(type: "bit", nullable: false),
                Enabled = table.Column<bool>(type: "bit", nullable: false),
                ModifiedRevision = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceSchedules", x => x.Id);
                table.ForeignKey(
                    name: "FK_DeviceSchedules_AlarmGroups_GroupId",
                    column: x => x.GroupId,
                    principalTable: "AlarmGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AckRecords_DeviceId_ReceiptId_Revision",
            table: "AckRecords",
            columns: new[] { "DeviceId", "ReceiptId", "Revision" });

        migrationBuilder.CreateIndex(
            name: "IX_DeviceSchedules_GroupId_DeviceId",
            table: "DeviceSchedules",
            columns: new[] { "GroupId", "DeviceId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SnapshotReceipts_DeviceId_Revision",
            table: "SnapshotReceipts",
            columns: new[] { "DeviceId", "Revision" });

        migrationBuilder.CreateIndex(
            name: "IX_Tombstones_DeviceId_ScheduleId_Cleared",
            table: "Tombstones",
            columns: new[] { "DeviceId", "ScheduleId", "Cleared" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AckRecords");

        migrationBuilder.DropTable(
            name: "Devices");

        migrationBuilder.DropTable(
            name: "DeviceSchedules");

        migrationBuilder.DropTable(
            name: "SnapshotReceipts");

        migrationBuilder.DropTable(
            name: "Tombstones");

        migrationBuilder.DropTable(
            name: "AlarmGroups");
    }
}
