using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyChartSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeeklyChartSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WeekEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    PreviousRank = table.Column<int>(type: "integer", nullable: true),
                    DeltaRank = table.Column<int>(type: "integer", nullable: true),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Artist = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CoverArtUrl = table.Column<string>(type: "text", nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    PlaysInWindow = table.Column<int>(type: "integer", nullable: false),
                    Basis = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyChartSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_weekly_chart_week_rank",
                table: "WeeklyChartSnapshots",
                columns: new[] { "WeekStartUtc", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_weekly_chart_week_track",
                table: "WeeklyChartSnapshots",
                columns: new[] { "WeekStartUtc", "TrackId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklyChartSnapshots");
        }
    }
}
