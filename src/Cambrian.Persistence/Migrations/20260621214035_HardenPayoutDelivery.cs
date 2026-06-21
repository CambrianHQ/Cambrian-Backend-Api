using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenPayoutDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payouts_CreatorId",
                table: "Payouts");

            migrationBuilder.AddColumn<string>(
                name: "StripeIdempotencyKey",
                table: "Payouts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeTransferId",
                table: "Payouts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_CreatorId_Status",
                table: "Payouts",
                columns: new[] { "CreatorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_StripeIdempotencyKey",
                table: "Payouts",
                column: "StripeIdempotencyKey",
                unique: true,
                filter: "\"StripeIdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payouts_CreatorId_Status",
                table: "Payouts");

            migrationBuilder.DropIndex(
                name: "IX_Payouts_StripeIdempotencyKey",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "StripeIdempotencyKey",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "StripeTransferId",
                table: "Payouts");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_CreatorId",
                table: "Payouts",
                column: "CreatorId");
        }
    }
}
