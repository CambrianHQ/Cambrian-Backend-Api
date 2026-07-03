using System.Reflection;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// RELEASE-GATE REGRESSION: EF Core's MigrationsAssembly only discovers
/// migration classes that carry BOTH [Migration] and [DbContext] — a
/// hand-written migration missing [DbContext] compiles, passes every test
/// (fixtures build schema from the model), and is then silently skipped by
/// `dotnet ef database update`, crashing prod on the missing columns.
/// AddMasteringLeasesAndArtwork shipped exactly this way. Never again.
/// </summary>
public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void Every_migration_class_is_discoverable_by_ef()
    {
        var persistenceAssembly = typeof(CambrianDbContext).Assembly;

        var migrationTypes = persistenceAssembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Migration).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<MigrationAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(migrationTypes);

        var undiscoverable = migrationTypes
            .Where(t => t.GetCustomAttribute<DbContextAttribute>()?.ContextType != typeof(CambrianDbContext))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            undiscoverable.Count == 0,
            "Migrations missing [DbContext(typeof(CambrianDbContext))] — EF will SILENTLY skip these on deploy: "
            + string.Join(", ", undiscoverable));
    }

    [Fact]
    public void The_growth_loop_migrations_are_present_and_discoverable()
    {
        var persistenceAssembly = typeof(CambrianDbContext).Assembly;
        var ids = persistenceAssembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .Where(id => id is not null)
            .ToList();

        Assert.Contains("20260702040000_AddMasteringLeasesAndArtwork", ids);
        Assert.Contains("20260702210842_AddWeeklyChartSnapshots", ids);
        Assert.Contains("20260702211808_AddWeeklyDigestFields", ids);
    }
}
