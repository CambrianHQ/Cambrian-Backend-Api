using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedAdminSettingsFeatureFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seeded Enabled=true to match the previous hardcoded-true admin/settings response —
            // deploying this migration must not silently disable payouts/moderation/marketplace.
            migrationBuilder.Sql("""
                INSERT INTO "FeatureFlags" ("Id", "Name", "Enabled", "RolloutPercentage", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), name, TRUE, 100, NOW(), NOW()
                FROM unnest(ARRAY[
                    'PayoutsEnabled',
                    'ModerationEnabled',
                    'MarketplaceEnabled',
                    'AllowExclusiveListings',
                    'RequireTrackReview'
                ]) AS name
                WHERE NOT EXISTS (
                    SELECT 1 FROM "FeatureFlags" f WHERE f."Name" = name
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "FeatureFlags"
                WHERE "Name" IN (
                    'PayoutsEnabled',
                    'ModerationEnabled',
                    'MarketplaceEnabled',
                    'AllowExclusiveListings',
                    'RequireTrackReview'
                );
                """);
        }
    }
}
