using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Data repair: accounts that bought (or trialed) a creator plan while
    /// holding a listener role were billed with nothing unlocked — capabilities
    /// derive from Role, and no subscription path ever promoted it (real
    /// support case, 2026-07). The code paths now promote on activation
    /// (ApplicationUser.EnsureCreatorRoleForTier); this backfills accounts
    /// already in the broken state. Idempotent; Admin rows untouched.
    /// </summary>
    public partial class PromoteCreatorRoleForPaidTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE affected integer;
                BEGIN
                    UPDATE "AspNetUsers"
                    SET "Role" = 'Creator'
                    WHERE lower("Role") NOT IN ('creator', 'admin')
                      AND ("CreatorTier" <> 0 OR lower("Tier") IN ('creator', 'pro'));
                    GET DIAGNOSTICS affected = ROW_COUNT;
                    RAISE NOTICE 'PromoteCreatorRoleForPaidTiers: promoted % account(s) to Creator role', affected;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data repair: the pre-promotion role values are not
            // recorded. Rolling back the schema must not demote creators.
        }
    }
}
