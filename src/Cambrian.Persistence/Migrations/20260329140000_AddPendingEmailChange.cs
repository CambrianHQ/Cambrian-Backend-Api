using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingEmailChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // C2 Phase A — additive columns for two-step email verification.
            // PendingEmail holds the requested new address until the user
            // clicks the verification link. The live Email is never changed
            // without the token being validated first.

            migrationBuilder.AddColumn<string>(
                name: "PendingEmail",
                table: "AspNetUsers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // SHA-256 hex hash of the verification token (64 chars)
            migrationBuilder.AddColumn<string>(
                name: "EmailChangeToken",
                table: "AspNetUsers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailChangeTokenExpiry",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PendingEmail",          table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "EmailChangeToken",      table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "EmailChangeTokenExpiry", table: "AspNetUsers");
        }
    }
}
