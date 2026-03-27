using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Enforce idempotency at the database level: each Stripe event ID
            // can only be recorded once, closing the race window in the
            // FirstOrDefaultAsync check.
            migrationBuilder.CreateIndex(
                name: "IX_StripeWebhookEvents_EventId",
                table: "StripeWebhookEvents",
                column: "EventId",
                unique: true);

            // Prevent duplicate purchases for the same Stripe checkout session.
            // Filtered index allows multiple NULLs (purchases without a session).
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_Purchases_StripeSessionId"
                ON "Purchases" ("StripeSessionId")
                WHERE "StripeSessionId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_StripeSessionId",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_StripeWebhookEvents_EventId",
                table: "StripeWebhookEvents");
        }
    }
}
