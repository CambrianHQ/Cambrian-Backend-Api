using System.Data.Common;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Cambrian.Persistence.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests.PlayCounts;

/// <summary>
/// The durable write path: StreamRepository.StartAsync records a qualifying play and updates the
/// TrackStats/CreatorStats projection in the SAME SaveChanges call — proving aggregation is
/// transactional (all-or-nothing), that duplicate/concurrent attempts at the same play can't
/// double-count (a real database UNIQUE constraint, not just an app-level check), and that a
/// mid-write failure leaves neither the event nor the counter change behind.
///
/// Uses its own SQLite databases (shared-cache mode so multiple real connections can see the
/// same in-memory database) rather than the shared CambrianApiFixture, so each test can freely
/// use separate connections for genuine concurrency and command interceptors for fault injection
/// without affecting any other test.
/// </summary>
public sealed class PlayCountWritePathTests
{
    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    private static (SqliteConnection KeepAlive, string ConnectionString) CreateSharedCacheDatabase()
    {
        var dbName = $"playcounts_write_{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        using (var pragma = keepAlive.CreateCommand())
        {
            // Serialize concurrent writers instead of failing fast with SQLITE_BUSY — mirrors
            // Postgres row-locking behavior (callers wait, they don't get spurious errors).
            pragma.CommandText = "PRAGMA busy_timeout = 10000;";
            pragma.ExecuteNonQuery();
        }
        using (var pragma = keepAlive.CreateCommand())
        {
            // These tests seed Tracks/Creators directly without a matching AspNetUsers row
            // (Track.CreatorId has an FK to it) — irrelevant to what's under test here, and the
            // shared CambrianApiFixture disables the same constraint for the same reason.
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }
        return (keepAlive, connectionString);
    }

    private static CambrianDbContext CreateDbContext(string connectionString, IEnumerable<IInterceptor>? interceptors = null)
    {
        var builder = new DbContextOptionsBuilder<CambrianDbContext>().UseSqlite(connectionString);
        if (interceptors is not null)
            builder.AddInterceptors(interceptors);
        var db = new CambrianDbContext(builder.Options);
        // Pragmas are per-connection, not per-database — every separate connection to the
        // shared-cache in-memory DB needs these set again, not just the keepalive one.
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 10000;");
        return db;
    }

    private static async Task<(Guid TrackId, Guid CreatorUuid)> SeedTrackAndCreatorAsync(string connectionString, string userId)
    {
        await using var db = CreateDbContext(connectionString);
        await db.Database.EnsureCreatedAsync();

        var creatorUuid = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        db.Creators.Add(new Creator { Id = creatorUuid, UserId = userId, Username = $"writer-{creatorUuid:N}", DisplayName = "Writer" });
        db.Tracks.Add(new Track { Id = trackId, Title = "Write Path Beat", CreatorId = userId, CreatorUuid = creatorUuid });
        await db.SaveChangesAsync();
        return (trackId, creatorUuid);
    }

    private static StreamRepository CreateStreamRepository(CambrianDbContext db, int minQualifyingSeconds = 0)
    {
        var config = minQualifyingSeconds == 0
            ? EmptyConfig()
            : new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["PlayCounts:MinQualifyingSeconds"] = minQualifyingSeconds.ToString() })
                .Build();
        return new StreamRepository(db, config, Substitute.For<ILogger<StreamRepository>>());
    }

    private static PlayCountService CreatePlayCountService(CambrianDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()), Substitute.For<ILogger<PlayCountService>>());

    // ── Aggregation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_QualifyingPlay_TransactionallyUpdatesTrackAndCreatorStats()
    {
        var (keepAlive, connStr) = CreateSharedCacheDatabase();
        await using var _ = keepAlive;
        const string userId = "creator-agg-1";
        var (trackId, creatorUuid) = await SeedTrackAndCreatorAsync(connStr, userId);

        await using var db = CreateDbContext(connStr);
        var repo = CreateStreamRepository(db);
        var playCounts = CreatePlayCountService(db);

        var (session, isNewPlay) = await repo.StartAsync(trackId, "listener-1", null);

        Assert.True(isNewPlay);
        Assert.True(session.Qualified);
        // No separate worker, no delay — the projection reflects the play immediately because
        // the increment happened in the SAME SaveChanges call as the event insert.
        Assert.Equal(1L, await playCounts.GetTrackPlayCountAsync(trackId));
        Assert.Equal(1L, await playCounts.GetCreatorTotalPlaysAsync(userId, creatorUuid));

        await repo.StartAsync(trackId, "listener-2", null);
        Assert.Equal(2L, await playCounts.GetTrackPlayCountAsync(trackId));
        Assert.Equal(2L, await playCounts.GetCreatorTotalPlaysAsync(userId, creatorUuid));
    }

    // ── Duplicate processing ─────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_SameUserRepeatWithinWindow_CollapsesToOnePlay_NoDoubleCount()
    {
        var (keepAlive, connStr) = CreateSharedCacheDatabase();
        await using var _ = keepAlive;
        const string userId = "creator-dup-1";
        var (trackId, _) = await SeedTrackAndCreatorAsync(connStr, userId);

        await using var db = CreateDbContext(connStr);
        var repo = CreateStreamRepository(db);
        var playCounts = CreatePlayCountService(db);

        var (first, firstIsNew) = await repo.StartAsync(trackId, "listener-1", null);
        var (second, secondIsNew) = await repo.StartAsync(trackId, "listener-1", null);

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1L, await playCounts.GetTrackPlayCountAsync(trackId));
        Assert.Equal(1, await db.StreamSessions.CountAsync(s => s.TrackId == trackId));
    }

    [Fact]
    public async Task DuplicateIdempotencyKey_RejectedByDatabaseConstraint_NotJustAppLogic()
    {
        // Proves the guarantee is a real UNIQUE constraint, not merely the repository's
        // check-before-insert — a raw attempt to insert a second row with the same key (as a
        // concurrent replica that raced past the pre-check would) is rejected by the database
        // itself, and the rejected attempt's staged counter increment never lands.
        var (keepAlive, connStr) = CreateSharedCacheDatabase();
        await using var _ = keepAlive;
        const string userId = "creator-dup-2";
        var (trackId, _) = await SeedTrackAndCreatorAsync(connStr, userId);

        await using var db1 = CreateDbContext(connStr);
        var repo = CreateStreamRepository(db1);
        var (winner, _) = await repo.StartAsync(trackId, "listener-1", null);

        await using var db2 = CreateDbContext(connStr);
        db2.StreamSessions.Add(new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            UserId = "listener-1",
            StartedAt = DateTime.UtcNow,
            IdempotencyKey = winner.IdempotencyKey,
            Qualified = true,
        });
        // Also stage a counter increment, mirroring what a real concurrent StartAsync would do —
        // it must roll back together with the rejected insert, in the same failed SaveChanges.
        var trackStat = await db2.TrackStats.FindAsync(trackId);
        trackStat!.PlayCount += 1;

        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());

        await using var verifyDb = CreateDbContext(connStr);
        var playCounts = CreatePlayCountService(verifyDb);
        Assert.Equal(1L, await playCounts.GetTrackPlayCountAsync(trackId));
        Assert.Equal(1, await verifyDb.StreamSessions.CountAsync(s => s.TrackId == trackId));
    }

    // ── Concurrent processing / multiple backend instances ──────────────────

    [Fact]
    public async Task StartAsync_ConcurrentReplicasSamePlay_ExactlyOneWinsAndCounterReflectsOne()
    {
        var (keepAlive, connStr) = CreateSharedCacheDatabase();
        await using var _ = keepAlive;
        const string userId = "creator-concurrent-1";
        var (trackId, _) = await SeedTrackAndCreatorAsync(connStr, userId);

        // Simulate several backend replicas racing to record the SAME listener's play at
        // (nearly) the same instant — each gets its own connection/DbContext, exactly like
        // separate processes would.
        const int replicaCount = 8;
        var tasks = Enumerable.Range(0, replicaCount).Select(async _ =>
        {
            await using var db = CreateDbContext(connStr);
            var repo = CreateStreamRepository(db);
            return await repo.StartAsync(trackId, "listener-1", null);
        });

        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Select(r => r.Session.Id).Distinct());
        Assert.Equal(1, results.Count(r => r.IsNewPlay));

        await using var verifyDb = CreateDbContext(connStr);
        Assert.Equal(1, await verifyDb.StreamSessions.CountAsync(s => s.TrackId == trackId));
        var playCounts = CreatePlayCountService(verifyDb);
        Assert.Equal(1L, await playCounts.GetTrackPlayCountAsync(trackId));
    }

    [Fact]
    public async Task StartAsync_ConcurrentReplicasDistinctListeners_NoEventIsLost()
    {
        var (keepAlive, connStr) = CreateSharedCacheDatabase();
        await using var _ = keepAlive;
        const string userId = "creator-concurrent-2";
        var (trackId, creatorUuid) = await SeedTrackAndCreatorAsync(connStr, userId);

        const int listenerCount = 10;
        var tasks = Enumerable.Range(0, listenerCount).Select(async i =>
        {
            await using var db = CreateDbContext(connStr);
            var repo = CreateStreamRepository(db);
            return await repo.StartAsync(trackId, $"listener-{i}", null);
        });

        var results = await Task.WhenAll(tasks);
        Assert.Equal(listenerCount, results.Select(r => r.Session.Id).Distinct().Count());
        Assert.All(results, r => Assert.True(r.IsNewPlay));

        await using var verifyDb = CreateDbContext(connStr);
        // Every event is durably present regardless of how the incremental counter fared under
        // concurrency — this is the "no accepted event is ever lost" guarantee. The counter
        // itself is allowed to drift under true concurrency (see PlayCountReconciliationTests);
        // it is a best-effort projection, not the source of truth.
        Assert.Equal(listenerCount, await verifyDb.StreamSessions.CountAsync(s => s.TrackId == trackId));

        var reconciliation = new PlayCountReconciliationService(verifyDb, Substitute.For<ILogger<PlayCountReconciliationService>>());
        await reconciliation.ReconcileAsync(new Application.DTOs.PlayCounts.ReconciliationOptions
        {
            TrackIds = new[] { trackId },
            DryRun = false,
            Repair = true,
        });

        await using var afterDb = CreateDbContext(connStr);
        var playCounts = CreatePlayCountService(afterDb);
        Assert.Equal((long)listenerCount, await playCounts.GetTrackPlayCountAsync(trackId));
        Assert.Equal((long)listenerCount, await playCounts.GetCreatorTotalPlaysAsync(userId, creatorUuid));
    }

    // ── Transaction failure ──────────────────────────────────────────────────

    private sealed class ThrowOnTrackStatsWriteInterceptor : DbCommandInterceptor
    {
        private static bool IsTrackStatsWrite(DbCommand command) =>
            command.CommandText.Contains("TrackStats", StringComparison.OrdinalIgnoreCase);

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (IsTrackStatsWrite(command))
                throw new InvalidOperationException("Simulated transaction failure while writing TrackStats.");
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsTrackStatsWrite(command))
                throw new InvalidOperationException("Simulated transaction failure while writing TrackStats.");
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (IsTrackStatsWrite(command))
                throw new InvalidOperationException("Simulated transaction failure while writing TrackStats.");
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsTrackStatsWrite(command))
                throw new InvalidOperationException("Simulated transaction failure while writing TrackStats.");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task StartAsync_WhenProjectionWriteFails_RollsBackTheEventToo()
    {
        var (keepAlive, connStr) = CreateSharedCacheDatabase();
        await using var _ = keepAlive;
        const string userId = "creator-txfail-1";
        var (trackId, _) = await SeedTrackAndCreatorAsync(connStr, userId);

        await using var faultyDb = CreateDbContext(connStr, new IInterceptor[] { new ThrowOnTrackStatsWriteInterceptor() });
        var repo = CreateStreamRepository(faultyDb);

        // The TrackStats write is staged in the SAME SaveChanges call as the StreamSession
        // insert; forcing it to throw must roll back the whole batch — the event must not be
        // left behind as an "orphaned" row with no matching counter update.
        await Assert.ThrowsAnyAsync<Exception>(() => repo.StartAsync(trackId, "listener-1", null));

        await using var verifyDb = CreateDbContext(connStr);
        Assert.Equal(0, await verifyDb.StreamSessions.CountAsync(s => s.TrackId == trackId));
        var playCounts = CreatePlayCountService(verifyDb);
        Assert.Equal(0L, await playCounts.GetTrackPlayCountAsync(trackId));
    }
}
