using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityAndGrowthFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TrendingScore",
                table: "Tracks",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "UseCase",
                table: "Tracks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "AnalyticsEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<bool>(
                name: "IsSimulated",
                table: "AnalyticsEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "activity_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    is_simulated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analytics_events_track_type_created",
                table: "AnalyticsEvents",
                columns: new[] { "TrackId", "EventType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_activity_items_type_created",
                table: "activity_items",
                columns: new[] { "type", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_activity_items_source_type",
                table: "activity_items",
                columns: new[] { "SourceId", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_items");

            migrationBuilder.DropIndex(
                name: "ix_analytics_events_track_type_created",
                table: "AnalyticsEvents");

            migrationBuilder.DropColumn(
                name: "TrendingScore",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "UseCase",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "IsSimulated",
                table: "AnalyticsEvents");

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "AnalyticsEvents",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }
    }
}
