using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for DownloadController which verifies ownership before allowing
/// downloads. Covers GUID validation, ownership enforcement, missing tracks,
/// and signed URL generation for both /download/{trackId} and /download/{trackId}/signed.
/// </summary>
public sealed class DownloadControllerTests
{
    private readonly IDownloadService _download = Substitute.For<IDownloadService>();
    private readonly DownloadController _controller;

    public DownloadControllerTests()
    {
        _controller = new DownloadController(_download);
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
        _download.GetDownloadUrlAsync(trackId, "user-no-access")
            .ThrowsAsync(new UnauthorizedAccessException("You must purchase this track before downloading."));

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
        _download.GetDownloadUrlAsync(trackId, "user-1")
            .ThrowsAsync(new KeyNotFoundException("Track audio not found."));

        var result = await _controller.Download(trackId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Download_Returns404_WhenTrackHasNullAudioUrl()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _download.GetDownloadUrlAsync(trackId, "user-1")
            .ThrowsAsync(new KeyNotFoundException("Track audio not found."));

        var result = await _controller.Download(trackId.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Download_ReturnsSignedUrl_WhenUserOwnsTrack()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _download.GetDownloadUrlAsync(trackId, "user-1")
            .Returns(new { url = "https://cdn.test/signed/beat.mp3" });

        var result = await _controller.Download(trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
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
        _download.GetSignedUrlAsync(trackId, "user-1")
            .ThrowsAsync(new UnauthorizedAccessException("You must purchase this track before downloading."));

        var result = await _controller.SignedUrl(trackId.ToString());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
    }

    [Fact]
    public async Task SignedUrl_ReturnsUrl_WhenOwned()
    {
        var trackId = Guid.NewGuid();
        SetupUser();
        _download.GetSignedUrlAsync(trackId, "user-1")
            .Returns(new { signedUrl = "https://cdn.test/signed", expiresAt = DateTime.UtcNow.AddMinutes(15) });

        var result = await _controller.SignedUrl(trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }
}
