using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicMetricsStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreatorStats",
                columns: table => new
                {
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    FollowerCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TrackCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TotalPlays = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    MonthlyPlays = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    SubscriberCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TipCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TipsReceivedCents = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LatestReleaseAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TrendingScore = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorStats", x => x.CreatorId);
                    table.ForeignKey(
                        name: "FK_CreatorStats_Creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackStats",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayCount = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LikeCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SalesCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TipCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TipTotalCents = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastPlayedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackStats", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_TrackStats_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreatorStats_FollowerCount",
                table: "CreatorStats",
                column: "FollowerCount");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorStats_TrendingScore",
                table: "CreatorStats",
                column: "TrendingScore");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreatorStats");

            migrationBuilder.DropTable(
                name: "TrackStats");
        }
    }
}
