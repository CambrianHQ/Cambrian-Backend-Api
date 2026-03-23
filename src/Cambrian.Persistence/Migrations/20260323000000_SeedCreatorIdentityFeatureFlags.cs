using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations;

/// <summary>
/// Seeds the "creator_profiles" and "username_routing" feature flags
/// (disabled, 0% rollout) so they exist before the rollout begins.
/// Additive only — does not modify existing flags.
/// </summary>
public partial class SeedCreatorIdentityFeatureFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            INSERT INTO ""FeatureFlags"" (""Id"", ""Name"", ""Enabled"", ""RolloutPercentage"", ""CreatedAt"", ""UpdatedAt"")
            VALUES
                (gen_random_uuid(), 'creator_profiles', FALSE, 0, NOW(), NOW()),
                (gen_random_uuid(), 'username_routing', FALSE, 0, NOW(), NOW())
            ON CONFLICT DO NOTHING;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            DELETE FROM ""FeatureFlags""
            WHERE ""Name"" IN ('creator_profiles', 'username_routing');
        ");
    }
}
