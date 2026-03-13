using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for DownloadController which verifies ownership before allowing
/// downloads. Covers GUID validation, ownership enforcement, missing tracks,
/// and signed URL generation for both /download/{trackId} and /download/{trackId}/signed.
/// </summary>
public sealed class DownloadControllerTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly ILibraryRepository _library = Substitute.For<ILibraryRepository>();
    private readonly DownloadController _controller;

    public DownloadControllerTests()
    {
        var logger = Substitute.For<ILogger<DownloadController>>();
        _controller = new DownloadController(_tracks, _storage, _library, logger);
    }

    private void SetupUser(string userId = "user-1")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── Download ──

    [Fact]
    public async Task Download_Returns400_WhenTrackIdNotGuid()
    {
        SetupUser();
        var result = await _controller.Download("not-a-guid");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("GUID", envelope.Error);
    }

    [Fact]
    public async Task Download_Returns403_WhenUserDoesNotOwnTrack()
    {
        var trackId = Guid.NewGuid();
        SetupUser("user-no-access");
        _library.GetByUserAndTrackAsync("user-no-access", trackId).Returns((LibraryItem?)null);

        var result = await _controller.Download(trackId.ToString());

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var envelope = Assert.IsType<ApiResponse<object?>>(forbidden.Value);
        Assert.Contains("purchase", envelope.Error);
    }

    [Fact]
    public async Task Download_Returns404_WhenTrackAudioNotFound()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _library.GetByUserAndTrackAsync("user-1", trackId)
            .Returns(new LibraryItem { Id = Guid.NewGuid() });
        _tracks.GetByIdAsync(trackId).Returns((Track?)null);

        var result = await _controller.Download(trackId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Download_Returns404_WhenTrackHasNullAudioUrl()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _library.GetByUserAndTrackAsync("user-1", trackId)
            .Returns(new LibraryItem { Id = Guid.NewGuid() });
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Beat",
            AudioUrl = null,
            CreatorId = "c1"
        });

        var result = await _controller.Download(trackId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Download_ReturnsFile_WhenUserOwnsTrack()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _library.GetByUserAndTrackAsync("user-1", trackId)
            .Returns(new LibraryItem { Id = Guid.NewGuid() });
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Beat",
            AudioUrl = "tracks/beat.mp3",
            CreatorId = "c1"
        });
        _storage.OpenReadAsync("tracks/beat.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }),
            ContentType = "audio/mpeg"
        });

        var result = await _controller.Download(trackId.ToString());

        Assert.IsType<FileStreamResult>(result);
    }

    // ── SignedUrl ──

    [Fact]
    public async Task SignedUrl_Returns400_WhenTrackIdNotGuid()
    {
        SetupUser();
        var result = await _controller.SignedUrl("bad-id");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SignedUrl_Returns403_WhenNotOwned()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _library.GetByUserAndTrackAsync("user-1", trackId).Returns((LibraryItem?)null);

        var result = await _controller.SignedUrl(trackId.ToString());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
    }

    [Fact]
    public async Task SignedUrl_ReturnsUrl_WhenOwned()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _library.GetByUserAndTrackAsync("user-1", trackId)
            .Returns(new LibraryItem { Id = Guid.NewGuid() });
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            Title = "Beat",
            AudioUrl = "tracks/beat.mp3",
            CreatorId = "c1"
        });
        _storage.GenerateSignedUrl("tracks/beat.mp3").Returns("https://cdn.test/signed");

        var result = await _controller.SignedUrl(trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }
}
