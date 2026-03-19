using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioFileHashToTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioFileHash",
                table: "Tracks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_CreatorId_AudioFileHash",
                table: "Tracks",
                columns: new[] { "CreatorId", "AudioFileHash" },
                filter: "\"AudioFileHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_CreatorId_AudioFileHash",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "AudioFileHash",
                table: "Tracks");
        }
    }
}
