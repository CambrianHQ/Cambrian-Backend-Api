using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumsLyricsBehindTheTrack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReleaseDate",
                table: "TrackCollections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "TrackCollections",
                type: "character varying(220)",
                maxLength: 220,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "TrackCollections",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "public");

            migrationBuilder.CreateTable(
                name: "AlbumTracks",
                columns: table => new
                {
                    AlbumId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumTracks", x => new { x.AlbumId, x.TrackId });
                });

            migrationBuilder.CreateTable(
                name: "TrackCreationProcesses",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Story = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    YoutubeUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ToolsUsed = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackCreationProcesses", x => x.TrackId);
                });

            migrationBuilder.CreateTable(
                name: "TrackLyrics",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Lyrics = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "en"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackLyrics", x => x.TrackId);
                });

            // ── Backfills (Postgres) ─────────────────────────────────────
            // Existing collections get a slug derived from their title with an
            // id suffix for guaranteed uniqueness — this must happen before the
            // unique (CreatorId, Slug) index below is created. New albums get
            // pretty slugs from the repository at create time.
            migrationBuilder.Sql("""
                UPDATE "TrackCollections"
                SET "Slug" = trim(both '-' from
                    left(regexp_replace(lower("Title"), '[^a-z0-9]+', '-', 'g'), 180)
                    || '-' || left("Id"::text, 8))
                WHERE "Slug" = '' OR "Slug" IS NULL;
                """);

            // Existing CSV memberships become AlbumTrack join rows (position =
            // CSV order). Tracks themselves are untouched — albums are
            // relationships only. Malformed CSV entries are skipped; duplicate
            // track ids within one album keep their first position.
            migrationBuilder.Sql("""
                INSERT INTO "AlbumTracks" ("AlbumId", "TrackId", "Position", "AddedAt")
                SELECT c."Id", trim(t.track_id)::uuid, (t.ord - 1)::int, c."UpdatedAt"
                FROM "TrackCollections" c
                CROSS JOIN LATERAL unnest(string_to_array(c."TrackIds", ',')) WITH ORDINALITY AS t(track_id, ord)
                WHERE coalesce(c."TrackIds", '') <> ''
                  AND trim(t.track_id) ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                ON CONFLICT ("AlbumId", "TrackId") DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TrackCollections_CreatorId_Slug",
                table: "TrackCollections",
                columns: new[] { "CreatorId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlbumTracks_AlbumId_Position",
                table: "AlbumTracks",
                columns: new[] { "AlbumId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_AlbumTracks_TrackId",
                table: "AlbumTracks",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumTracks");

            migrationBuilder.DropTable(
                name: "TrackCreationProcesses");

            migrationBuilder.DropTable(
                name: "TrackLyrics");

            migrationBuilder.DropIndex(
                name: "IX_TrackCollections_CreatorId_Slug",
                table: "TrackCollections");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                table: "TrackCollections");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "TrackCollections");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "TrackCollections");
        }
    }
}
