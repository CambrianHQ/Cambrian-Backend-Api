using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProvenanceAuthorshipSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CommercialRightsVerified",
                table: "Tracks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "Tracks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Signature",
                table: "Tracks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedAt",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProvenanceAnchors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Chain = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MerkleRoot = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RootTxRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MerkleProof = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    AnchoredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvenanceAnchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProvenanceAnchors_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackAuthorships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Edits = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ArrangementNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    LyricsAuthored = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AiDisclosure = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackAuthorships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackAuthorships_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_ContentHash",
                table: "Tracks",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceAnchors_BatchId",
                table: "ProvenanceAnchors",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceAnchors_Status",
                table: "ProvenanceAnchors",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ProvenanceAnchors_TrackId",
                table: "ProvenanceAnchors",
                column: "TrackId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackAuthorships_TrackId",
                table: "TrackAuthorships",
                column: "TrackId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvenanceAnchors");

            migrationBuilder.DropTable(
                name: "TrackAuthorships");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_ContentHash",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "CommercialRightsVerified",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "SignedAt",
                table: "Tracks");
        }
    }
}
