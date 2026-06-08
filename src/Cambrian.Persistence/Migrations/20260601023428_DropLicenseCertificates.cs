using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLicenseCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_LicenseCertificates_LicenseId",
                table: "Purchases");

            migrationBuilder.DropTable(
                name: "LicenseCertificates");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_LicenseId",
                table: "Purchases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LicenseCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerId = table.Column<string>(type: "text", nullable: false),
                    CreatorId = table.Column<string>(type: "text", nullable: false),
                    PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedUses = table.Column<string>(type: "text", nullable: true),
                    CopyrightOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LicenseType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Restrictions = table.Column<string>(type: "text", nullable: true),
                    TrackId = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    UsageType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false, defaultValue: "personal")
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
                name: "IX_Purchases_LicenseId",
                table: "Purchases",
                column: "LicenseId",
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

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_LicenseCertificates_LicenseId",
                table: "Purchases",
                column: "LicenseId",
                principalTable: "LicenseCertificates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
