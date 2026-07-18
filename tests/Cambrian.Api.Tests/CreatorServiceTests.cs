using Cambrian.Application.DTOs.Creator;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

public sealed class CreatorServiceTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly IPayoutRepository _payouts = Substitute.For<IPayoutRepository>();
    private readonly IWalletRepository _wallet = Substitute.For<IWalletRepository>();
    private readonly IStreamRepository _streams = Substitute.For<IStreamRepository>();
    private readonly ICreatorIdentityRepository _creators = Substitute.For<ICreatorIdentityRepository>();
    private readonly ICreatorProfileRepository _profiles = Substitute.For<ICreatorProfileRepository>();
    private readonly ICreatorMilestoneRepository _milestones = Substitute.For<ICreatorMilestoneRepository>();
    private readonly CreatorService _sut;

    public CreatorServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);

        _sut = new CreatorService(
            _tracks,
            _purchases,
            _payouts,
            _wallet,
            _streams,
            users,
            _creators,
            _profiles,
            _milestones,
            Substitute.For<ILogger<CreatorService>>());
    }

    [Fact]
    public async Task GetDashboardAsync_UsesTrackSummariesInsteadOfFullTrackLoad()
    {
        var creatorUuid = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        _creators.GetCreatorIdForUserAsync("creator-1").Returns(creatorUuid);
        _wallet.GetTotalCreditsAsync("creator-1").Returns(1234);
        _wallet.GetCreditsAfterAsync("creator-1", Arg.Any<DateTime>()).Returns(234);
        _tracks.GetDashboardTrackSummariesAsync("creator-1", creatorUuid).Returns(
        [
            new CreatorDashboardTrackSummary
            {
                Id = trackId,
                Title = "Night Drive",
                CoverArtUrl = "covers/night-drive.jpg"
            }
        ]);
        _streams.GetPlayCountsByTrackIdsAsync(Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { trackId })))
            .Returns(new Dictionary<Guid, long> { [trackId] = 9 });
        _purchases.GetCompletedCountsByTrackIdsAsync(Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { trackId })))
            .Returns(new Dictionary<Guid, int> { [trackId] = 3 });
        _wallet.GetCreditsByTrackAsync("creator-1", Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { trackId })))
            .Returns(new Dictionary<Guid, long> { [trackId] = 4500 });
        _tracks.GetByCreatorIdAsync("creator-1", creatorUuid).Throws(new Exception("dashboard should not load full tracks"));

        var result = await _sut.GetDashboardAsync("creator-1");

        Assert.Equal(1234, result.EarningsCents);
        Assert.Equal(234, result.WeeklyEarningsCents);
        Assert.Equal(9, result.TotalPlays);
        Assert.Equal(3, result.TotalSales);
        Assert.Equal(33.33m, result.ConversionRate);
        var track = Assert.Single(result.Tracks);
        Assert.Equal(trackId.ToString(), track.Id);
        Assert.Equal("Night Drive", track.Title);
        Assert.Equal("covers/night-drive.jpg", track.CoverArtUrl);
        Assert.Equal(3, track.Sales);
        Assert.Equal(9, track.Plays);
        Assert.Equal(4500, track.EarnedCents);
        await _tracks.Received(1).GetDashboardTrackSummariesAsync("creator-1", creatorUuid);
        await _tracks.DidNotReceive().GetByCreatorIdAsync("creator-1", creatorUuid);
    }

    [Fact]
    public async Task GetTracksAsync_UsesTrackSummariesInsteadOfFullTrackLoad()
    {
        var creatorUuid = Guid.NewGuid();
        _creators.GetCreatorIdForUserAsync("creator-1").Returns(creatorUuid);
        _profiles.GetByUserIdAsync("creator-1").Returns(new CreatorProfileDto
        {
            UserId = "creator-1",
            Slug = "creator-one",
            ProfileImageUrl = "profiles/creator-one.jpg"
        });
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            DisplayName = "Creator One",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Pro
        });

        var sut = new CreatorService(
            _tracks,
            _purchases,
            _payouts,
            _wallet,
            _streams,
            users,
            _creators,
            _profiles,
            _milestones,
            Substitute.For<ILogger<CreatorService>>());

        _tracks.GetCreatorTrackSummariesAsync("creator-1", creatorUuid).Returns(
        [
            new CreatorTrackSummary
            {
                Id = Guid.NewGuid(),
                CambrianTrackId = "CAMB-TRK-1001",
                Title = "Night Drive",
                Description = "Late city energy",
                Genre = "Synthwave",
                Mood = "energetic",
                Tempo = "128 BPM",
                Tags = ["retro", "neon"],
                Instrumental = true,
                Visibility = "public",
                Price = 19.99m,
                NonExclusivePriceCents = 1999,
                ExclusivePriceCents = 9999,
                CopyrightBuyoutPriceCents = 14999,
                ExclusiveSold = false,
                Status = "available",
                LicenseType = "non-exclusive",
                Duration = "3:12",
                AudioUrl = "tracks/night-drive.mp3",
                CoverArtUrl = "covers/night-drive.jpg",
                CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        ]);
        _tracks.GetByCreatorIdAsync("creator-1", creatorUuid).Throws(new Exception("track list should not load full tracks"));

        var result = await sut.GetTracksAsync("creator-1", 1, 50);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.False(result.HasNextPage);
        Assert.False(result.HasPreviousPage);
        var track = Assert.Single(result.Items);
        Assert.Equal("CAMB-TRK-1001", track.CambrianTrackId);
        Assert.Equal("Night Drive", track.Title);
        Assert.Equal("Synthwave", track.Genre);
        Assert.Null(track.PrimaryGenre);
        Assert.Equal("Synthwave", track.Subgenre);
        Assert.Equal("creator-one", track.CreatorSlug);
        Assert.Equal("profiles/creator-one.jpg", track.CreatorProfileImageUrl);
        Assert.Equal("Creator One", track.Artist);
        await _tracks.Received(1).GetCreatorTrackSummariesAsync("creator-1", creatorUuid);
        await _tracks.DidNotReceive().GetByCreatorIdAsync("creator-1", creatorUuid);
    }

    [Fact]
    public async Task GetRevenueAsync_UsesDashboardTrackSummariesInsteadOfFullTrackLoad()
    {
        var creatorUuid = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        _creators.GetCreatorIdForUserAsync("creator-1").Returns(creatorUuid);
        _tracks.GetDashboardTrackSummariesAsync("creator-1", creatorUuid).Returns(
        [
            new CreatorDashboardTrackSummary { Id = trackId, Title = "Night Drive" }
        ]);
        _purchases.GetByTrackIdAsync(trackId).Returns(
        [
            new Purchase { AmountCents = 1200, Status = "completed" }
        ]);
        _payouts.GetByCreatorIdAsync("creator-1").Returns(new List<Payout>());
        _tracks.GetByCreatorIdAsync("creator-1", creatorUuid).Throws(new Exception("revenue should not load full tracks"));

        var result = await _sut.GetRevenueAsync("creator-1");

        Assert.NotNull(result);
        await _tracks.Received(1).GetDashboardTrackSummariesAsync("creator-1", creatorUuid);
        await _tracks.DidNotReceive().GetByCreatorIdAsync("creator-1", creatorUuid);
    }
}
