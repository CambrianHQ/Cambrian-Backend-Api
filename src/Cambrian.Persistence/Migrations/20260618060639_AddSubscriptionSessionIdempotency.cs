using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionSessionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeSessionId",
                table: "Subscriptions",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_StripeSessionId",
                table: "Subscriptions",
                column: "StripeSessionId",
                unique: true,
                filter: "\"StripeSessionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_StripeSessionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "StripeSessionId",
                table: "Subscriptions");
        }
    }
}
