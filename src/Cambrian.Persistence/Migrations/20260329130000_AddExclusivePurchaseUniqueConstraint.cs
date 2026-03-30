using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExclusivePurchaseUniqueConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // C1 — database-level invariant: only one exclusive purchase per track.
            // This is the last line of defense after the atomic SQL check-and-set
            // in StripeWebhookService. If two concurrent webhooks somehow both
            // pass the check-and-set, the unique index ensures one of them is
            // rejected with a constraint violation before a duplicate row is inserted.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"ux_purchases_track_exclusive\" " +
                "ON \"Purchases\"(\"TrackId\") WHERE \"LicenseType\" = 'exclusive';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"ux_purchases_track_exclusive\";");
        }
    }
}
