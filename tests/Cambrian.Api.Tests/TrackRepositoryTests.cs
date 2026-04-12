using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Tests;

public sealed class TrackRepositoryTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly TrackRepository _repository;

    public TrackRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new CambrianDbContext(options);
        _repository = new TrackRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task UpdateAsync_DoesNotReattachDuplicateApplicationUser_WhenTrackIsDetached()
    {
        var userId = Guid.NewGuid().ToString();
        var trackId = Guid.NewGuid();

        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "creator",
            NormalizedUserName = "CREATOR",
            Email = "creator@test.com",
            NormalizedEmail = "CREATOR@TEST.COM",
            Role = "Creator",
            Tier = "creator",
            Status = "active"
        });

        _db.Tracks.Add(new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-UPDATET1",
            CreatorId = userId,
            Title = "Before",
            Description = "Old description",
            Genre = "Old",
            NonExclusivePriceCents = 1000,
            ExclusivePriceCents = 2000,
            CopyrightBuyoutPriceCents = 3000,
            Status = "available",
            Visibility = "public",
            Tags = new List<string> { "old-tag" }
        });

        await _db.SaveChangesAsync();

        _ = await _db.Users.FirstAsync(u => u.Id == userId);

        var detachedTrack = new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-UPDATET1",
            CreatorId = userId,
            Title = "After",
            Description = "New description",
            Genre = "Hip-Hop",
            NonExclusivePriceCents = 1500,
            ExclusivePriceCents = 2500,
            CopyrightBuyoutPriceCents = 3500,
            Status = "available",
            Visibility = "public",
            Tags = new List<string> { "drill", "dark" },
            Creator = new ApplicationUser
            {
                Id = userId,
                UserName = "creator",
                NormalizedUserName = "CREATOR",
                Email = "creator@test.com",
                NormalizedEmail = "CREATOR@TEST.COM"
            }
        };

        var exception = await Record.ExceptionAsync(() => _repository.UpdateAsync(detachedTrack));

        Assert.Null(exception);

        var updated = await _db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Equal("After", updated.Title);
        Assert.Equal("New description", updated.Description);
        Assert.Equal(1500, updated.NonExclusivePriceCents);
        Assert.Equal(["drill", "dark"], updated.Tags);
    }
}
