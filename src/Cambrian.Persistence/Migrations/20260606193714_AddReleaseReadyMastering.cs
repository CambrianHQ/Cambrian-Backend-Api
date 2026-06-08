using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseReadyMastering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiDisclosureDdex",
                table: "Tracks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AiGenerated",
                table: "Tracks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MasteringJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<string>(type: "text", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    Engine = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceKey = table.Column<string>(type: "text", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MasteredWavKey = table.Column<string>(type: "text", nullable: true),
                    MasteredMp3Key = table.Column<string>(type: "text", nullable: true),
                    EngineRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PreviewKey = table.Column<string>(type: "text", nullable: true),
                    InputLufs = table.Column<double>(type: "double precision", nullable: true),
                    OutputLufs = table.Column<double>(type: "double precision", nullable: true),
                    OutputTruePeakDbtp = table.Column<double>(type: "double precision", nullable: true),
                    TargetLufs = table.Column<double>(type: "double precision", nullable: false),
                    TargetTruePeakDbtp = table.Column<double>(type: "double precision", nullable: false),
                    ValidationReportJson = table.Column<string>(type: "text", nullable: true),
                    ChargedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasteringJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_CreatorId",
                table: "MasteringJobs",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_CreatorId_ChargedAt",
                table: "MasteringJobs",
                columns: new[] { "CreatorId", "ChargedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_Status_CreatedAt",
                table: "MasteringJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_TrackId",
                table: "MasteringJobs",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "AiDisclosureDdex",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "AiGenerated",
                table: "Tracks");
        }
    }
}
