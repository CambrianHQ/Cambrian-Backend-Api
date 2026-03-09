using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests for the Library:
///   GET /library → list saved tracks
///   POST /library → save a track
///   DELETE /library/{trackId} → remove a track
///   GET /library/purchased-track-ids → purchased id list
/// </summary>
public sealed class LibraryTests
{
    private readonly ILibraryRepository _library = Substitute.For<ILibraryRepository>();
    private readonly IPurchaseRepository _purchases = Substitute.For<IPurchaseRepository>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly LibraryService _sut;

    public LibraryTests()
    {
        _sut = new LibraryService(_library, _purchases, _tracks);
    }

    private static ClaimsPrincipal MakeUser(string userId = "user-1") =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }));

    [Fact]
    public async Task GetUserId_ThrowsUnauthorized_WhenNoClaims()
    {
        var emptyUser = new ClaimsPrincipal(new ClaimsIdentity());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.GetLibraryAsync(emptyUser));
    }

    [Fact]
    public async Task GetLibraryAsync_ReturnsEmptyList_WhenNoItems()
    {
        _library.GetByUserIdAsync("user-1").Returns(new List<LibraryItem>());
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var result = await _sut.GetLibraryAsync(MakeUser());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetLibraryAsync_MapsTitleFromTrack_WhenAvailable()
    {
        var items = new List<LibraryItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = "user-1",
                TrackId = Guid.NewGuid(),
                Title = "Fallback Title",
                Track = new Track
                {
                    Title = "Real Track Title",
                    Creator = new ApplicationUser { DisplayName = "Artist" },
                    CreatorId = "c1"
                }
            }
        };
        _library.GetByUserIdAsync("user-1").Returns(items);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var result = await _sut.GetLibraryAsync(MakeUser());

        Assert.Single(result);
        Assert.Equal("Real Track Title", result.First().Title);
        Assert.Equal("Artist", result.First().Artist);
    }

    [Fact]
    public async Task GetLibraryAsync_FallsBackToItemTitle_WhenTrackIsNull()
    {
        var items = new List<LibraryItem>
        {
            new()
            {
                Id = Guid.NewGuid(),
                UserId = "user-1",
                TrackId = Guid.NewGuid(),
                Title = "Saved Title",
                Artist = "Saved Artist",
                Track = null!
            }
        };
        _library.GetByUserIdAsync("user-1").Returns(items);
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>());

        var result = await _sut.GetLibraryAsync(MakeUser());

        Assert.Equal("Saved Title", result.First().Title);
        Assert.Equal("Saved Artist", result.First().Artist);
    }

    [Fact]
    public async Task SaveAsync_DoesNotDuplicate_WhenAlreadySaved()
    {
        var trackId = Guid.NewGuid();
        _library.GetByUserAndTrackAsync("user-1", trackId)
            .Returns(new LibraryItem { Id = Guid.NewGuid() });

        await _sut.SaveAsync(MakeUser(), new LibrarySaveRequest { TrackId = trackId.ToString() });

        await _library.DidNotReceive().AddAsync(Arg.Any<LibraryItem>());
    }

    [Fact]
    public async Task SaveAsync_ThrowsKeyNotFound_WhenTrackDoesNotExist()
    {
        var trackId = Guid.NewGuid();
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);
        _tracks.GetByIdAsync(trackId).Returns((Track?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _sut.SaveAsync(MakeUser(), new LibrarySaveRequest { TrackId = trackId.ToString() }));
    }

    [Fact]
    public async Task SaveAsync_AddsItem_WhenNewAndTrackExists()
    {
        var trackId = Guid.NewGuid();
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "New Beat",
            AudioUrl = "https://cdn.test/beat.mp3",
            Creator = new ApplicationUser { DisplayName = "DJ" },
            CreatorId = "c1"
        });

        await _sut.SaveAsync(MakeUser(), new LibrarySaveRequest { TrackId = trackId.ToString() });

        await _library.Received(1).AddAsync(Arg.Is<LibraryItem>(item =>
            item.UserId == "user-1" &&
            item.TrackId == trackId &&
            item.Title == "New Beat" &&
            item.Artist == "DJ"));
    }

    [Fact]
    public async Task RemoveAsync_CallsRepository()
    {
        var trackId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _library.GetByUserAndTrackAsync("user-1", trackId)
            .Returns(new LibraryItem { Id = itemId });

        await _sut.RemoveAsync(MakeUser(), trackId.ToString());

        await _library.Received(1).RemoveAsync(itemId);
    }

    [Fact]
    public async Task RemoveAsync_DoesNothing_WhenItemNotFound()
    {
        var trackId = Guid.NewGuid();
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);

        await _sut.RemoveAsync(MakeUser(), trackId.ToString());

        await _library.DidNotReceive().RemoveAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task GetPurchasedTrackIdsAsync_OnlyReturnsCompleted()
    {
        var completedTrackId = Guid.NewGuid();
        var pendingTrackId = Guid.NewGuid();
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>
        {
            new() { TrackId = completedTrackId, Status = "completed", BuyerId = "user-1" },
            new() { TrackId = pendingTrackId, Status = "pending", BuyerId = "user-1" }
        });

        var result = await _sut.GetPurchasedTrackIdsAsync(MakeUser());

        Assert.Single(result);
        Assert.Contains(completedTrackId.ToString(), result);
        Assert.DoesNotContain(pendingTrackId.ToString(), result);
    }

    [Fact]
    public async Task GetPurchasedTrackIdsAsync_DeduplicatesTrackIds()
    {
        var trackId = Guid.NewGuid();
        _purchases.GetByBuyerIdAsync("user-1").Returns(new List<Purchase>
        {
            new() { TrackId = trackId, Status = "completed", BuyerId = "user-1" },
            new() { TrackId = trackId, Status = "completed", BuyerId = "user-1" }
        });

        var result = await _sut.GetPurchasedTrackIdsAsync(MakeUser());

        Assert.Single(result);
    }

    [Fact]
    public async Task GetLibraryAsync_AcceptsSubClaim()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, "user-sub-1")
        }));
        _library.GetByUserIdAsync("user-sub-1").Returns(new List<LibraryItem>());
        _purchases.GetByBuyerIdAsync("user-sub-1").Returns(new List<Purchase>());

        var result = await _sut.GetLibraryAsync(user);

        Assert.Empty(result);
    }
}
