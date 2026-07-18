using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QualifiedPlayLedgerAndChartFreshness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StreamSessions_TrackId",
                table: "StreamSessions");

            migrationBuilder.AddColumn<DateTime>(
                name: "DataThroughUtc",
                table: "WeeklyChartSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LifetimePlays",
                table: "WeeklyChartSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "WeeklyQualifiedPlays",
                table: "WeeklyChartSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LegacyPlayCount",
                table: "TrackStats",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "QualifiedPlayCount",
                table: "TrackStats",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReconciledAtUtc",
                table: "TrackStats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ActivePlaybackSeconds",
                table: "StreamSessions",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "AnonymousSessionHash",
                table: "StreamSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "StreamSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOwnerPreview",
                table: "StreamSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastStartedAtUtc",
                table: "StreamSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListenerKeyHash",
                table: "StreamSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualificationStatus",
                table: "StreamSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "legacy_unqualified");

            migrationBuilder.AddColumn<double>(
                name: "QualificationThresholdSeconds",
                table: "StreamSessions",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<DateTime>(
                name: "QualifiedAtUtc",
                table: "StreamSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasEligibleAtStart",
                table: "StreamSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Historical StreamSessions do not contain the active-time, pause, seek,
            // eligibility, or stable-listener evidence required to reconstruct qualified
            // events, so no QualifiedPlayEvents are fabricated. The legacy baseline is
            // GREATEST(displayed PlayCount, raw historical-session count): where
            // TrackStats undercounted raw sessions, the displayed lifetime count may
            // INCREASE (never decrease). Where TrackStats did not exist, snapshot the
            // raw historical-session count once.
            migrationBuilder.Sql("""
                INSERT INTO "TrackStats" (
                    "TrackId", "PlayCount", "LegacyPlayCount", "QualifiedPlayCount",
                    "LikeCount", "SalesCount", "TipCount", "TipTotalCents",
                    "LastPlayedAt", "UpdatedAt", "ReconciledAtUtc")
                SELECT
                    sessions."TrackId",
                    COUNT(*)::bigint,
                    COUNT(*)::bigint,
                    0,
                    0,
                    0,
                    0,
                    0,
                    MAX(sessions."StartedAt"),
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM "StreamSessions" AS sessions
                LEFT JOIN "TrackStats" AS stats ON stats."TrackId" = sessions."TrackId"
                WHERE stats."TrackId" IS NULL
                GROUP BY sessions."TrackId";

                WITH historical AS (
                    SELECT "TrackId", COUNT(*)::bigint AS "SessionCount"
                    FROM "StreamSessions"
                    GROUP BY "TrackId"
                )
                UPDATE "TrackStats" AS stats
                SET
                    "LegacyPlayCount" = GREATEST(stats."PlayCount", COALESCE(historical."SessionCount", 0)),
                    "PlayCount" = GREATEST(stats."PlayCount", COALESCE(historical."SessionCount", 0)),
                    "QualifiedPlayCount" = 0,
                    "ReconciledAtUtc" = CURRENT_TIMESTAMP
                FROM historical
                WHERE historical."TrackId" = stats."TrackId";

                UPDATE "TrackStats"
                SET
                    "LegacyPlayCount" = "PlayCount",
                    "QualifiedPlayCount" = 0,
                    "ReconciledAtUtc" = CURRENT_TIMESTAMP
                WHERE "ReconciledAtUtc" IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "QualifiedPlayEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ListenerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ListenerKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AnonymousSessionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PlaybackSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    QualificationBasis = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActivePlaybackSeconds = table.Column<double>(type: "double precision", nullable: false),
                    ThresholdSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AggregatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QualifiedPlayEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QualifiedPlayEvents_StreamSessions_PlaybackSessionId",
                        column: x => x.PlaybackSessionId,
                        principalTable: "StreamSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_QualifiedPlayEvents_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stream_sessions_track_listener_started",
                table: "StreamSessions",
                columns: new[] { "TrackId", "ListenerKeyHash", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_stream_sessions_idempotency_key",
                table: "StreamSessions",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_qualified_play_events_aggregated_at",
                table: "QualifiedPlayEvents",
                column: "AggregatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "ix_qualified_play_events_listener_track_qualified_at",
                table: "QualifiedPlayEvents",
                columns: new[] { "ListenerKeyHash", "TrackId", "QualifiedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ix_qualified_play_events_track_qualified_at",
                table: "QualifiedPlayEvents",
                columns: new[] { "TrackId", "QualifiedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "ux_qualified_play_events_idempotency_key",
                table: "QualifiedPlayEvents",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_qualified_play_events_playback_session",
                table: "QualifiedPlayEvents",
                column: "PlaybackSessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QualifiedPlayEvents");

            migrationBuilder.DropIndex(
                name: "ix_stream_sessions_track_listener_started",
                table: "StreamSessions");

            migrationBuilder.DropIndex(
                name: "ux_stream_sessions_idempotency_key",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "DataThroughUtc",
                table: "WeeklyChartSnapshots");

            migrationBuilder.DropColumn(
                name: "LifetimePlays",
                table: "WeeklyChartSnapshots");

            migrationBuilder.DropColumn(
                name: "WeeklyQualifiedPlays",
                table: "WeeklyChartSnapshots");

            migrationBuilder.DropColumn(
                name: "LegacyPlayCount",
                table: "TrackStats");

            migrationBuilder.DropColumn(
                name: "QualifiedPlayCount",
                table: "TrackStats");

            migrationBuilder.DropColumn(
                name: "ReconciledAtUtc",
                table: "TrackStats");

            migrationBuilder.DropColumn(
                name: "ActivePlaybackSeconds",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "AnonymousSessionHash",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "IsOwnerPreview",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "LastStartedAtUtc",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "ListenerKeyHash",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "QualificationStatus",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "QualificationThresholdSeconds",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "QualifiedAtUtc",
                table: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "WasEligibleAtStart",
                table: "StreamSessions");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_TrackId",
                table: "StreamSessions",
                column: "TrackId");
        }
    }
}
