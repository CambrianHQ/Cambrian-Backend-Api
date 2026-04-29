using Cambrian.Application.Services;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cambrian.Api.Tests.PerformanceHooks;

[Trait("Category", "PerformanceHook")]
public sealed class EntitlementPerformanceHookTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;

    public EntitlementPerformanceHookTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new CambrianDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task EntitlementLookup_FanOut_CompletesConsistently()
    {
        var userId = Guid.NewGuid().ToString("N");
        var creatorId = Guid.NewGuid().ToString("N");
        var trackId = Guid.NewGuid();

        _db.Users.AddRange(
            new Cambrian.Domain.Entities.ApplicationUser
            {
                Id = userId,
                Email = "fanout-buyer@test.com",
                NormalizedEmail = "FANOUT-BUYER@TEST.COM",
                UserName = "fanout-buyer@test.com",
                NormalizedUserName = "FANOUT-BUYER@TEST.COM"
            },
            new Cambrian.Domain.Entities.ApplicationUser
            {
                Id = creatorId,
                Email = "fanout-creator@test.com",
                NormalizedEmail = "FANOUT-CREATOR@TEST.COM",
                UserName = "fanout-creator@test.com",
                NormalizedUserName = "FANOUT-CREATOR@TEST.COM"
            });
        _db.Tracks.Add(new Cambrian.Domain.Entities.Track
        {
            Id = trackId,
            CambrianTrackId = $"CAMB-TRK-{trackId.ToString()[..8].ToUpperInvariant()}",
            Title = "Fanout Beat",
            CreatorId = creatorId,
            AudioUrl = "tracks/fanout.mp3"
        });
        _db.Purchases.Add(new Cambrian.Domain.Entities.Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = userId,
            TrackId = trackId,
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var repository = new PurchaseRepository(_db);
        var sut = new EntitlementService(
            repository,
            new EntitlementRepository(_db),
            NullLogger<EntitlementService>.Instance);

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => sut.CanDownloadAsync(userId, trackId)));

        results.Should().OnlyContain(x => x);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
