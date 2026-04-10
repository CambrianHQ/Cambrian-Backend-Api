using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New verification fields used by AuthService.IssueAndSendVerificationLinkAsync.
            // The token stored is a SHA-256 hash of "{userId}.{randomBytes}" — the raw value
            // is only sent once via the verification link.
            migrationBuilder.AddColumn<string>(
                name: "EmailVerificationToken",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailVerificationTokenExpiry",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: every existing account predates the verification flow and is
            // implicitly trusted, so mark them as verified. Without this, the new
            // VerifiedEmail policy would lock out the entire existing user base.
            migrationBuilder.Sql(
                "UPDATE \"AspNetUsers\" SET \"EmailVerified\" = TRUE WHERE \"EmailVerified\" = FALSE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailVerificationToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailVerificationTokenExpiry",
                table: "AspNetUsers");
        }
    }
}
