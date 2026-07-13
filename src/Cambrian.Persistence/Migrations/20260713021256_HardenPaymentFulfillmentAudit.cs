using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenPaymentFulfillmentAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DisputedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastStripeInvoiceId",
                table: "Subscriptions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentFailedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisputedAt",
                table: "ReleaseCreditPurchases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RefundedAmountCents",
                table: "ReleaseCreditPurchases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAt",
                table: "ReleaseCreditPurchases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaymentFailedAt",
                table: "FanSubscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisputedAt",
                table: "AuthorshipRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentStatus",
                table: "AuthorshipRecords",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "RefundedAt",
                table: "AuthorshipRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "AuthorshipRecords",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthorshipRecords_StripePaymentIntentId",
                table: "AuthorshipRecords",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthorshipRecords_StripePaymentIntentId",
                table: "AuthorshipRecords");

            migrationBuilder.DropColumn(
                name: "DisputedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "LastStripeInvoiceId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PaymentFailedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "DisputedAt",
                table: "ReleaseCreditPurchases");

            migrationBuilder.DropColumn(
                name: "RefundedAmountCents",
                table: "ReleaseCreditPurchases");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                table: "ReleaseCreditPurchases");

            migrationBuilder.DropColumn(
                name: "PaymentFailedAt",
                table: "FanSubscriptions");

            migrationBuilder.DropColumn(
                name: "DisputedAt",
                table: "AuthorshipRecords");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                table: "AuthorshipRecords");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                table: "AuthorshipRecords");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "AuthorshipRecords");
        }
    }
}
