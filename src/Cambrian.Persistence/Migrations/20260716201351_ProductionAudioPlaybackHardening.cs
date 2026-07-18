using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductionAudioPlaybackHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "TrackLyrics",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "MediaReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RemediationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TracksInspected = table.Column<int>(type: "integer", nullable: false),
                    ObjectsInspected = table.Column<int>(type: "integer", nullable: false),
                    FindingCount = table.Column<int>(type: "integer", nullable: false),
                    UnresolvedPublishedTrackFailures = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrackMedia",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Draft"),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StateChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ChecksumSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    ValidationVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackMedia", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_TrackMedia_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaReconciliationFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    FindingType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Resolution = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaReconciliationFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaReconciliationFindings_MediaReconciliationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "MediaReconciliationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_findings_run_track",
                table: "MediaReconciliationFindings",
                columns: new[] { "RunId", "TrackId" });

            migrationBuilder.CreateIndex(
                name: "ix_media_reconciliation_runs_started",
                table: "MediaReconciliationRuns",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "ix_track_media_object_key",
                table: "TrackMedia",
                column: "ObjectKey");

            migrationBuilder.CreateIndex(
                name: "ix_track_media_state_validated",
                table: "TrackMedia",
                columns: new[] { "State", "ValidatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaReconciliationFindings");

            migrationBuilder.DropTable(
                name: "TrackMedia");

            migrationBuilder.DropTable(
                name: "MediaReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "TrackLyrics");
        }
    }
}
