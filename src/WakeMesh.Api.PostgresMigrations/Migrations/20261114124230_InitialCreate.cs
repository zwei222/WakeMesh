using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WakeMesh.Api.PostgresMigrations.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail-closed precheck. The RLS/least-privilege statements at the end of this migration
            // are meaningless without the bootstrapped roles, and every table must be owned by the
            // migrator. deploy/postgres/00-bootstrap.sql must have run once as `postgres`.
            migrationBuilder.Sql(
                """
                DO $wm$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_migrator') THEN
                    RAISE EXCEPTION 'WakeMesh bootstrap missing: role wakemesh_migrator does not exist';
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_api') THEN
                    RAISE EXCEPTION 'WakeMesh bootstrap missing: role wakemesh_api does not exist';
                  END IF;
                  IF current_user <> 'wakemesh_migrator' THEN
                    RAISE EXCEPTION 'WakeMesh migrations must run as wakemesh_migrator, not %', current_user;
                  END IF;
                END $wm$;
                """);

            migrationBuilder.EnsureSchema(
                name: "wakemesh");

            migrationBuilder.CreateTable(
                name: "AckRecords",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: false),
                    FailuresJson = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AckRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlarmGroups",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Repeat = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DaysOrDateJson = table.Column<string>(type: "text", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExcludeHolidays = table.Column<bool>(type: "boolean", nullable: false),
                    ExcludedDatesJson = table.Column<string>(type: "text", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CredentialHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DesiredRevision = table.Column<long>(type: "bigint", nullable: false),
                    AppliedRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PushToken = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    PushTokenUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PushNotificationsCapable = table.Column<bool>(type: "boolean", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushOutbox",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DesiredRevision = table.Column<long>(type: "bigint", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotReceipts",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedSuccess = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tombstones",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtRevision = table.Column<long>(type: "bigint", nullable: false),
                    Cleared = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tombstones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceSchedules",
                schema: "wakemesh",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<string>(type: "character varying(64)", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Time = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SoundKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Volume = table.Column<int>(type: "integer", nullable: false),
                    Vibrate = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ModifiedRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceSchedules_AlarmGroups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "wakemesh",
                        principalTable: "AlarmGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AckRecords_DeviceId_ReceiptId_Revision",
                schema: "wakemesh",
                table: "AckRecords",
                columns: new[] { "DeviceId", "ReceiptId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSchedules_GroupId_DeviceId",
                schema: "wakemesh",
                table: "DeviceSchedules",
                columns: new[] { "GroupId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushOutbox_DeviceId_DesiredRevision_Status",
                schema: "wakemesh",
                table: "PushOutbox",
                columns: new[] { "DeviceId", "DesiredRevision", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PushOutbox_Status_LeaseExpiresAt",
                schema: "wakemesh",
                table: "PushOutbox",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PushOutbox_Status_NextAttemptAt",
                schema: "wakemesh",
                table: "PushOutbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotReceipts_DeviceId_Revision",
                schema: "wakemesh",
                table: "SnapshotReceipts",
                columns: new[] { "DeviceId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_Tombstones_DeviceId_ScheduleId_Cleared",
                schema: "wakemesh",
                table: "Tombstones",
                columns: new[] { "DeviceId", "ScheduleId", "Cleared" });

            // Row level security + least privilege.
            // ENABLE, not FORCE: the owner (migrator) must keep owner-bypass so pg_dump and the
            // deploy path work. The runtime role is never the owner, so RLS applies to it.
            // Exactly one permissive ALL policy for wakemesh_api; no default privileges are set, so
            // a future table without its own GRANT fails closed with 42501.
            foreach (var table in new[]
                     {
                         "AckRecords", "AlarmGroups", "Devices", "DeviceSchedules",
                         "PushOutbox", "SnapshotReceipts", "Tombstones",
                     })
            {
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE wakemesh."{table}" ENABLE ROW LEVEL SECURITY;
                     GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE wakemesh."{table}" TO wakemesh_api;
                     CREATE POLICY wakemesh_api_all ON wakemesh."{table}" AS PERMISSIVE FOR ALL TO wakemesh_api USING (true) WITH CHECK (true);
                     """);
            }

            // Migration metadata lives in the same private schema, with RLS on and NO policy and NO
            // grant: the runtime role gets 42501, and even a misexposed schema returns 0 rows.
            migrationBuilder.Sql(
                """
                ALTER TABLE wakemesh."__EFMigrationsHistory" ENABLE ROW LEVEL SECURITY;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AckRecords",
                schema: "wakemesh");

            migrationBuilder.DropTable(
                name: "Devices",
                schema: "wakemesh");

            migrationBuilder.DropTable(
                name: "DeviceSchedules",
                schema: "wakemesh");

            migrationBuilder.DropTable(
                name: "PushOutbox",
                schema: "wakemesh");

            migrationBuilder.DropTable(
                name: "SnapshotReceipts",
                schema: "wakemesh");

            migrationBuilder.DropTable(
                name: "Tombstones",
                schema: "wakemesh");

            migrationBuilder.DropTable(
                name: "AlarmGroups",
                schema: "wakemesh");
        }
    }
}
