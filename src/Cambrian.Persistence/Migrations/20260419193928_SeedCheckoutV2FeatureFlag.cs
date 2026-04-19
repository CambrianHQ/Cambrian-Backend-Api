using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Seeds the "checkout_v2" feature flag (enabled, 100% rollout) so the
    /// frontend's checkout UI is available on fresh deploys without a manual
    /// admin API call. The flag was previously expected by the frontend but
    /// never registered in the backend, causing every "License Track" button
    /// to render as "Licensing coming soon".
    /// Additive only — does not modify the flag if it already exists.
    /// </summary>
    public partial class SeedCheckoutV2FeatureFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""FeatureFlags"" (""Id"", ""Name"", ""Enabled"", ""RolloutPercentage"", ""CreatedAt"", ""UpdatedAt"")
                VALUES (gen_random_uuid(), 'checkout_v2', TRUE, 100, NOW(), NOW())
                ON CONFLICT DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""FeatureFlags""
                WHERE ""Name"" = 'checkout_v2';
            ");
        }
    }
}
