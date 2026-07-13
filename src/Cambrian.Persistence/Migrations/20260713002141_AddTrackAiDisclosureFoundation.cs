using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations;

public sealed partial class AddTrackAiDisclosureFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TrackAiDisclosures",
            columns: table => new
            {
                TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                Classification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                AiVocals = table.Column<bool>(type: "boolean", nullable: true),
                AiPrimaryInstruments = table.Column<bool>(type: "boolean", nullable: true),
                AiComposition = table.Column<bool>(type: "boolean", nullable: true),
                AiLyrics = table.Column<bool>(type: "boolean", nullable: true),
                AiPostProduction = table.Column<bool>(type: "boolean", nullable: true),
                AiArtwork = table.Column<bool>(type: "boolean", nullable: true),
                AiVideo = table.Column<bool>(type: "boolean", nullable: true),
                GeneratorTool = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ModelVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                CreationDate = table.Column<DateOnly>(type: "date", nullable: true),
                CommercialUseLicenseBasis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                VoiceLikenessAuthorization = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                HumanWrittenLyrics = table.Column<bool>(type: "boolean", nullable: true),
                HumanVocals = table.Column<bool>(type: "boolean", nullable: true),
                HumanInstruments = table.Column<bool>(type: "boolean", nullable: true),
                ArrangementEditing = table.Column<bool>(type: "boolean", nullable: true),
                DawWork = table.Column<bool>(type: "boolean", nullable: true),
                CollaboratorsJson = table.Column<string>(type: "text", nullable: true),
                HumanContributionNarrative = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                Version = table.Column<int>(type: "integer", nullable: false),
                IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                CorrectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TrackAiDisclosures", x => x.TrackId);
                table.ForeignKey("FK_TrackAiDisclosures_Tracks_TrackId", x => x.TrackId, "Tracks", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TrackAiDisclosureRevisions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                SnapshotJson = table.Column<string>(type: "text", nullable: false),
                ChangedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TrackAiDisclosureRevisions", x => x.Id);
                table.ForeignKey("FK_TrackAiDisclosureRevisions_Tracks_TrackId", x => x.TrackId, "Tracks", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TrackAiDisclosureRevisions_TrackId_Version",
            table: "TrackAiDisclosureRevisions",
            columns: new[] { "TrackId", "Version" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TrackAiDisclosureRevisions");
        migrationBuilder.DropTable(name: "TrackAiDisclosures");
    }
}
