using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cambrian.Api.Tests;

/// <summary>
/// Applies every committed EF migration against a fresh Postgres instance.
/// This is the only test that exercises the real migration pipeline — every
/// other test seeds the schema via EnsureCreated on SQLite, which bypasses
/// migrations entirely. If a new migration is malformed or depends on a prior
/// migration that was edited retroactively, this test is the canary.
///
/// Source of Postgres:
///   1. If env var `CAMBRIAN_TEST_POSTGRES` is set, use it (preferred on CI
///      where a Postgres service container is already running, e.g. the
///      integration-tests job).
///   2. Otherwise, spin up a Testcontainers Postgres (requires Docker).
///
/// Opt-in via trait Category=Postgres. CI or local runs without Docker and
/// without the env var can filter this out with `--filter "Category!=Postgres"`.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class MigrationApplyTests : IAsyncLifetime
{
    private const string SchemaName = "cambrian_migration_test";

    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;
    private string? _skipReason;

    public MigrationApplyTests()
    {
        var externalConn = Environment.GetEnvironmentVariable("CAMBRIAN_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(externalConn))
        {
            _connectionString = externalConn;
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            try
            {
                _container = new PostgreSqlBuilder()
                    .WithImage("postgres:16-alpine")
                    .WithDatabase("cambrian_migrations_test")
                    .WithUsername("cambrian")
                    .WithPassword("cambrian")
                    .Build();
            }
            catch (Exception ex)
            {
                _skipReason = $"Docker-backed migration test unavailable: {ex.Message}";
                return;
            }
        }

        if (_container is not null)
        {
            try
            {
                await _container.StartAsync();
                _connectionString = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                _skipReason = $"Docker-backed migration test unavailable: {ex.Message}";
            }
            return;
        }

        // Isolate this run in its own schema so the test can be re-run against
        // a shared Postgres without bleeding into other databases.
        await using var admin = new NpgsqlConnection(_connectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE; CREATE SCHEMA {SchemaName};";
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = SchemaName,
        };
        _connectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        // External Postgres: drop the schema we created so re-runs start fresh.
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(_connectionString) { SearchPath = null };
            await using var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE;";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    [Fact]
    public async Task All_Migrations_Apply_Cleanly_On_Empty_Database()
    {
        if (!string.IsNullOrWhiteSpace(_skipReason))
            return;

        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        await using var db = new CambrianDbContext(options);

        // Applies every migration under src/Cambrian.Persistence/Migrations.
        // Throws if a migration is malformed or references removed objects.
        await db.Database.MigrateAsync();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        Assert.NotEmpty(applied);
        Assert.Empty(pending);

        // A smoke query proves the schema is actually usable after migration —
        // just migration history being populated isn't enough.
        var userCount = await db.Users.CountAsync();
        Assert.Equal(0, userCount);

        var flagCount = await db.FeatureFlags.CountAsync();
        Assert.True(flagCount >= 0);
    }
}
