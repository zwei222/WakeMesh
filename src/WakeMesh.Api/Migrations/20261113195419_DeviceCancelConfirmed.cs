using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WakeMesh.Api.Migrations;

/// <inheritdoc />
public partial class _20261113195419_DeviceCancelConfirmed : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CancelConfirmed",
            table: "Devices",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CancelConfirmed",
            table: "Devices");
    }
}
