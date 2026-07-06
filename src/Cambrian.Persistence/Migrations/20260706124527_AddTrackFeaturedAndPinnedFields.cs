using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackFeaturedAndPinnedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FeaturedAt",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeaturedByUserId",
                table: "Tracks",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFeatured",
                table: "Tracks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "Tracks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedAt",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinnedByUserId",
                table: "Tracks",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeaturedAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "FeaturedByUserId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "IsFeatured",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "PinnedAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "PinnedByUserId",
                table: "Tracks");
        }
    }
}
