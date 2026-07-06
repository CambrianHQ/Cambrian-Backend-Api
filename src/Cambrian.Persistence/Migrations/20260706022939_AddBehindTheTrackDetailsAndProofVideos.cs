using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBehindTheTrackDetailsAndProofVideos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DAW",
                table: "TrackCreationProcesses",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HumanContributionNotes",
                table: "TrackCreationProcesses",
                type: "character varying(5000)",
                maxLength: 5000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductionNotes",
                table: "TrackCreationProcesses",
                type: "character varying(5000)",
                maxLength: 5000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptNotes",
                table: "TrackCreationProcesses",
                type: "character varying(5000)",
                maxLength: 5000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VocalChain",
                table: "TrackCreationProcesses",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrackVideoProofs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    VideoType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "public"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackVideoProofs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackVideoProofs_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackVideoProofs_TrackId_SortOrder",
                table: "TrackVideoProofs",
                columns: new[] { "TrackId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackVideoProofs");

            migrationBuilder.DropColumn(
                name: "DAW",
                table: "TrackCreationProcesses");

            migrationBuilder.DropColumn(
                name: "HumanContributionNotes",
                table: "TrackCreationProcesses");

            migrationBuilder.DropColumn(
                name: "ProductionNotes",
                table: "TrackCreationProcesses");

            migrationBuilder.DropColumn(
                name: "PromptNotes",
                table: "TrackCreationProcesses");

            migrationBuilder.DropColumn(
                name: "VocalChain",
                table: "TrackCreationProcesses");
        }
    }
}
