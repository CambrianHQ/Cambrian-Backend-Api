using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Data-repair migration: fixes verified creators and admin-upgraded users
    /// whose Pro tier fields were not persisted because they were processed by
    /// old code that lacked the tier-upgrade logic in VerifyCreatorAsync and
    /// UpgradeCreatorTierAsync.
    ///
    /// The code fix is already deployed — this migration repairs rows that were
    /// written before the fix.
    /// </summary>
    public partial class RepairStaleTierFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix 1: All verified creators MUST have Pro tier (business rule).
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET    "CreatorTier"         = 1,
                       "Tier"                = 'pro',
                       "SubscriptionStatus"  = 'Active',
                       "SubscriptionEndDate" = NULL
                WHERE  "VerifiedCreator" = true
                  AND  ("CreatorTier" <> 1 OR "Tier" <> 'pro' OR "SubscriptionStatus" <> 'Active');
                """);

            // Fix 2: Users upgraded to "pro" via admin bulk/individual action
            // whose old code didn't persist all tier fields.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" u
                SET    "CreatorTier"         = 1,
                       "Tier"                = 'pro',
                       "SubscriptionStatus"  = 'Active',
                       "SubscriptionEndDate" = NULL
                FROM   "AuditLogs" a
                WHERE  a."Action" = 'upgrade_creator_tier'
                  AND  a."Details" LIKE '% to pro'
                  AND  a."Details" LIKE '%' || u."Email" || '%'
                  AND  (u."CreatorTier" <> 1 OR u."Tier" <> 'pro' OR u."SubscriptionStatus" <> 'Active');
                """);

            // Fix 3: Users in verify_creator audit log whose VerifiedCreator flag
            // was not set (edge case from partial old-code failures).
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" u
                SET    "VerifiedCreator"     = true,
                       "Role"                = CASE WHEN u."Role" = 'User' THEN 'Creator' ELSE u."Role" END,
                       "CreatorTier"         = 1,
                       "Tier"                = 'pro',
                       "SubscriptionStatus"  = 'Active',
                       "SubscriptionEndDate" = NULL
                FROM   "AuditLogs" a
                WHERE  a."Action" = 'verify_creator'
                  AND  a."Details" LIKE '%' || u."Email" || '%'
                  AND  u."VerifiedCreator" = false;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-repair — not reversible (original values were incorrect).
        }
    }
}
