using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Adds the first-class Creators table with UUID PK, unique username index,
    /// and adds CreatorUuid FK column to Tracks.
    /// Backfills CreatorUuid from existing CreatorId → ApplicationUser → Creator mapping.
    /// </summary>
    public partial class AddCreatorsIdentityTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Creators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Username = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Bio = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, defaultValue: ""),
                    ProfileImageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CoverImageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SocialLinks = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Creators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Creators_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Creators_UserId",
                table: "Creators",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Creators_Username",
                table: "Creators",
                column: "Username",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatorUuid",
                table: "Tracks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_CreatorUuid",
                table: "Tracks",
                column: "CreatorUuid");

            migrationBuilder.AddForeignKey(
                name: "FK_Tracks_Creators_CreatorUuid",
                table: "Tracks",
                column: "CreatorUuid",
                principalTable: "Creators",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill: create Creator rows for every user with tracks or Tier='creator',
            // then set CreatorUuid on all matching Tracks.
            migrationBuilder.Sql(@"
                INSERT INTO ""Creators"" (""Id"", ""UserId"", ""Username"", ""DisplayName"", ""Bio"", ""CreatedAt"", ""UpdatedAt"")
                SELECT
                    gen_random_uuid(),
                    u.""Id"",
                    LOWER(REGEXP_REPLACE(SPLIT_PART(u.""Email"", '@', 1), '[^a-z0-9]', '-', 'gi')),
                    u.""DisplayName"",
                    '',
                    NOW(),
                    NOW()
                FROM ""AspNetUsers"" u
                WHERE u.""Tier"" = 'creator'
                   OR EXISTS (SELECT 1 FROM ""Tracks"" t WHERE t.""CreatorId"" = u.""Id"")
                ON CONFLICT DO NOTHING;

                WITH dupes AS (
                    SELECT ""Id"", ""Username"",
                           ROW_NUMBER() OVER (PARTITION BY ""Username"" ORDER BY ""CreatedAt"") AS rn
                    FROM ""Creators""
                )
                UPDATE ""Creators"" SET ""Username"" = ""Creators"".""Username"" || '-' || dupes.rn::text
                FROM dupes
                WHERE ""Creators"".""Id"" = dupes.""Id"" AND dupes.rn > 1;

                UPDATE ""Tracks"" t
                SET ""CreatorUuid"" = c.""Id""
                FROM ""Creators"" c
                WHERE t.""CreatorId"" = c.""UserId""
                  AND t.""CreatorUuid"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tracks_Creators_CreatorUuid",
                table: "Tracks");

            migrationBuilder.DropTable(
                name: "Creators");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_CreatorUuid",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "CreatorUuid",
                table: "Tracks");
        }
    }
}
