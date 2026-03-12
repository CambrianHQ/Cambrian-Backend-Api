using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackIdLicenseCertificateUsageTypeSearchFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Add CambrianTrackId as NULLABLE so existing rows aren't blocked.
            migrationBuilder.AddColumn<string>(
                name: "CambrianTrackId",
                table: "Tracks",
                type: "character varying(25)",
                maxLength: 25,
                nullable: true);

            // Step 2: Backfill existing rows with unique CAMB-TRK-XXXXXXXX identifiers.
            migrationBuilder.Sql(
                """
                UPDATE "Tracks"
                SET "CambrianTrackId" = 'CAMB-TRK-' || UPPER(SUBSTRING(REPLACE(gen_random_uuid()::text, '-', '') FROM 1 FOR 8))
                WHERE "CambrianTrackId" IS NULL OR "CambrianTrackId" = '';
                """);

            // Step 3: Now make the column NOT NULL.
            migrationBuilder.AlterColumn<string>(
                name: "CambrianTrackId",
                table: "Tracks",
                type: "character varying(25)",
                maxLength: 25,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Instrumental",
                table: "Tracks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "Tracks",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tempo",
                table: "Tracks",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsageType",
                table: "Purchases",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "personal");

            migrationBuilder.CreateTable(
                name: "LicenseCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    BuyerId = table.Column<string>(type: "text", nullable: false),
                    CreatorId = table.Column<string>(type: "text", nullable: false),
                    PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LicenseType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UsageType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "personal"),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllowedUses = table.Column<string>(type: "text", nullable: true),
                    Restrictions = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicenseCertificates_AspNetUsers_BuyerId",
                        column: x => x.BuyerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseCertificates_AspNetUsers_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LicenseCertificates_Purchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_CambrianTrackId",
                table: "Tracks",
                column: "CambrianTrackId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LicenseCertificates_BuyerId",
                table: "LicenseCertificates",
                column: "BuyerId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseCertificates_CreatorId",
                table: "LicenseCertificates",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseCertificates_PurchaseId",
                table: "LicenseCertificates",
                column: "PurchaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicenseCertificates");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_CambrianTrackId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "CambrianTrackId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Instrumental",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Tempo",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "UsageType",
                table: "Purchases");
        }
    }
}
