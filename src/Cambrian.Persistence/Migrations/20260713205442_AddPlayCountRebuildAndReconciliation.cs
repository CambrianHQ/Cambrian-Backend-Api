using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayCountRebuildAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StreamSessions_TrackId",
                table: "StreamSessions");

            migrationBuilder.AddColumn<long>(
                name: "UniqueListenerCount",
                table: "TrackStats",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "AnonymousKey",
                table: "StreamSessions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            // Added nullable first: a single literal default ("") would collide across every
            // pre-existing row and fail the unique index created below. Each historical row
            // instead gets a distinct, clearly-marked "legacy:{Id}" key via the backfill, then
            // the column is tightened to NOT NULL once every row has a real value.
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "StreamSessions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            // Historical plays default to qualified — a real recompute from real rows under the
            // "every session counts" rule in effect when they were recorded (and still the
            // platform default today via PlayCounts:MinQualifyingSeconds == 0), not fabricated data.
            migrationBuilder.AddColumn<bool>(
                name: "Qualified",
                table: "StreamSessions",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                "UPDATE \"StreamSessions\" SET \"IdempotencyKey\" = 'legacy:' || \"Id\"::text " +
                "WHERE \"IdempotencyKey\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                table: "StreamSessions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UniqueListenerCount",
                table: "CreatorStats",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "PlayCountReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    RepairRequested = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BatchSize = table.Column<int>(type: "integer", nullable: false),
                    TracksScanned = table.Column<int>(type: "integer", nullable: false),
                    MismatchesFound = table.Column<int>(type: "integer", nullable: false),
                    MismatchesRepaired = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "running"),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayCountReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayCountReconciliationEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredPlayCount = table.Column<long>(type: "bigint", nullable: false),
                    ComputedPlayCount = table.Column<long>(type: "bigint", nullable: false),
                    StoredUniqueListenerCount = table.Column<long>(type: "bigint", nullable: false),
                    ComputedUniqueListenerCount = table.Column<long>(type: "bigint", nullable: false),
                    Repaired = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayCountReconciliationEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayCountReconciliationEntries_PlayCountReconciliationRuns_~",
                        column: x => x.RunId,
                        principalTable: "PlayCountReconciliationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stream_sessions_track_qualified",
                table: "StreamSessions",
                columns: new[] { "TrackId", "Qualified" });

            migrationBuilder.CreateIndex(
                name: "ix_stream_sessions_track_user_started",
                table: "StreamSessions",
                columns: new[] { "TrackId", "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_stream_sessions_idempotency_key",
                table: "StreamSessions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_play_count_reconciliation_entries_run_id",
                table: "PlayCountReconciliationEntries",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "ix_play_count_reconciliation_entries_track_id",
                table: "PlayCountReconciliationEntries",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "ix_play_count_reconciliation_runs_started_at",
                table: "PlayCountReconciliationRuns",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayCountReconciliationEntries");

            migrationBuilder.DropTable(
                name: "PlayCountReconciliationRuns");

            migrationBuilder.DropIndex(
                name: "ix_stream_sessions_track_qualified",
                table: "StreamSessions");

            migrationBuilder.DropIndex(
                name: "ix_stream_sessions_track_user_started",
                table: "StreamSessions");

            migrationBuilder.DropIndex(
                name: "ux_stream_sessions_idempotency_key",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "UniqueListenerCount",
                table: "TrackStats");

            migrationBuilder.DropColumn(
                name: "AnonymousKey",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "Qualified",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "UniqueListenerCount",
                table: "CreatorStats");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_TrackId",
                table: "StreamSessions",
                column: "TrackId");
        }
    }
}
