using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WakeMesh.Api.Migrations;

/// <inheritdoc />
public partial class _20261114000000_PushOutboxLeaseAndNotificationCapable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PushNotificationsCapable",
            table: "Devices",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "LeaseOwner",
            table: "PushOutbox",
            type: "nvarchar(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LeaseExpiresAt",
            table: "PushOutbox",
            type: "datetimeoffset",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PushOutbox_Status_LeaseExpiresAt",
            table: "PushOutbox",
            columns: new[] { "Status", "LeaseExpiresAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PushOutbox_Status_LeaseExpiresAt",
            table: "PushOutbox");

        migrationBuilder.DropColumn(
            name: "LeaseOwner",
            table: "PushOutbox");

        migrationBuilder.DropColumn(
            name: "LeaseExpiresAt",
            table: "PushOutbox");

        migrationBuilder.DropColumn(
            name: "PushNotificationsCapable",
            table: "Devices");
    }
}
