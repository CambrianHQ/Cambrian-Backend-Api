using Cambrian.Application.DTOs.Creator;
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
        _streams.GetPlayCountsByTrackIdsAsync(Arg.Is<List<Guid>>(ids => ids.SequenceEqual([trackId])))
            .Returns(new Dictionary<Guid, int> { [trackId] = 9 });
        _purchases.GetCompletedCountsByTrackIdsAsync(Arg.Is<List<Guid>>(ids => ids.SequenceEqual([trackId])))
            .Returns(new Dictionary<Guid, int> { [trackId] = 3 });
        _wallet.GetCreditsByTrackAsync("creator-1", Arg.Is<List<Guid>>(ids => ids.SequenceEqual([trackId])))
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
        _tracks.Received(1).GetDashboardTrackSummariesAsync("creator-1", creatorUuid);
        _tracks.DidNotReceive().GetByCreatorIdAsync("creator-1", creatorUuid);
    }
}
