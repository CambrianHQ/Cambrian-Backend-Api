using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionStripeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Subscription Stripe fields ──
            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "Subscriptions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionId",
                table: "Subscriptions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            // ── Exclusive purchase unique constraint (from AddExclusivePurchaseUniqueConstraint) ──
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"ux_purchases_track_exclusive\" " +
                "ON \"Purchases\"(\"TrackId\") WHERE \"LicenseType\" = 'exclusive';");

            // ── Pending email change columns (from AddPendingEmailChange) ──
            migrationBuilder.AddColumn<string>(
                name: "PendingEmail",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailChangeToken",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailChangeTokenExpiry",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // ── Email verified flag (from AddEmailVerified) ──
            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionId",
                table: "Subscriptions");

            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"ux_purchases_track_exclusive\";");

            migrationBuilder.DropColumn(
                name: "PendingEmail",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailChangeToken",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailChangeTokenExpiry",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailVerified",
                table: "AspNetUsers");
        }
    }
}
