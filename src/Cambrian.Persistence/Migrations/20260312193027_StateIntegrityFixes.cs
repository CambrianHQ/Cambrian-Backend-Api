using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StateIntegrityFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "Purchases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Purchases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LicenseId",
                table: "Purchases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSessionId",
                table: "Purchases",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseId",
                table: "Library",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_LicenseId",
                table: "Purchases",
                column: "LicenseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_StripeSessionId",
                table: "Purchases",
                column: "StripeSessionId",
                unique: true,
                filter: "\"StripeSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Library_PurchaseId",
                table: "Library",
                column: "PurchaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_Library_Purchases_PurchaseId",
                table: "Library",
                column: "PurchaseId",
                principalTable: "Purchases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_LicenseCertificates_LicenseId",
                table: "Purchases",
                column: "LicenseId",
                principalTable: "LicenseCertificates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Library_Purchases_PurchaseId",
                table: "Library");

            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_LicenseCertificates_LicenseId",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_LicenseId",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_StripeSessionId",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_Library_PurchaseId",
                table: "Library");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "LicenseId",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "StripeSessionId",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "PurchaseId",
                table: "Library");
        }
    }
}
