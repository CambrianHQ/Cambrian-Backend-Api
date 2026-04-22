using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    public partial class SeedStripeConnectEnabledFeatureFlag : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "FeatureFlags" ("Id", "Name", "Enabled", "RolloutPercentage", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), 'StripeConnectEnabled', TRUE, 100, NOW(), NOW()
                WHERE NOT EXISTS (
                    SELECT 1 FROM "FeatureFlags" WHERE "Name" = 'StripeConnectEnabled'
                );
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "FeatureFlags"
                WHERE "Name" = 'StripeConnectEnabled';
                """);
        }
    }
}
