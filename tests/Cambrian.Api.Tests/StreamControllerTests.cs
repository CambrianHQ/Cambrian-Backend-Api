using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for StreamController covering stream listing, signed URL generation,
/// session start, and session stop. Validates GUID validation and
/// authorization of start/stop endpoints.
/// </summary>
public sealed class StreamControllerTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly IStreamRepository _streams = Substitute.For<IStreamRepository>();
    private readonly StreamController _controller;

    public StreamControllerTests()
    {
        _controller = new StreamController(_tracks, _storage, _streams);
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

    // ── List ──

    [Fact]
    public async Task List_ReturnsOk()
    {
        _tracks.BrowseAsync().Returns(new List<Track>
        {
            new()
            {
                Id = Guid.NewGuid(), Title = "Beat 1", AudioUrl = "url1",
                Creator = new ApplicationUser { DisplayName = "DJ" }, CreatorId = "c1"
            }
        });

        var result = await _controller.List();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Stream by ID ──

    [Fact]
    public async Task Stream_Returns400_WhenTrackIdNotGuid()
    {
        var result = await _controller.Stream("invalid");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("GUID", envelope.Error);
    }

    [Fact]
    public async Task Stream_Returns404_WhenTrackNotFound()
    {
        var id = Guid.NewGuid();
        _tracks.GetByIdAsync(id).Returns((Track?)null);

        var result = await _controller.Stream(id.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Stream_ReturnsSignedUrl_WhenFound()
    {
        var id = Guid.NewGuid();
        _tracks.GetByIdAsync(id).Returns(new Track
        {
            Id = id, Title = "Beat", AudioUrl = "audio/beat.mp3", CreatorId = "c1"
        });
        _storage.GenerateSignedUrl("audio/beat.mp3").Returns("https://cdn.test/signed");

        var result = await _controller.Stream(id.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Start (Authorize) ──

    [Fact]
    public async Task Start_CreatesSession_FromBody()
    {
        SetupUser();
        var trackId = Guid.NewGuid();
        _streams.StartAsync(trackId, "user-1").Returns(new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            UserId = "user-1"
        });

        var result = await _controller.Start(
            new StreamController.StreamStartRequest { TrackId = trackId.ToString() });

        Assert.IsType<OkObjectResult>(result);
        await _streams.Received(1).StartAsync(trackId, "user-1");
    }

    [Fact]
    public async Task Start_CreatesSession_FromQueryString()
    {
        SetupUser();
        var trackId = Guid.NewGuid();
        _streams.StartAsync(trackId, "user-1").Returns(new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            UserId = "user-1"
        });

        var result = await _controller.Start(null, trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Start_UsesEmptyGuid_WhenTrackIdInvalid()
    {
        SetupUser();
        _streams.StartAsync(Guid.Empty, "user-1").Returns(new StreamSession
        {
            Id = Guid.NewGuid(),
            TrackId = Guid.Empty,
            UserId = "user-1"
        });

        var result = await _controller.Start(
            new StreamController.StreamStartRequest { TrackId = "not-a-guid" });

        Assert.IsType<OkObjectResult>(result);
        await _streams.Received(1).StartAsync(Guid.Empty, "user-1");
    }

    // ── Stop (Authorize) ──

    [Fact]
    public async Task Stop_StopsSession_WhenIdValid()
    {
        SetupUser();
        var sessionId = Guid.NewGuid();

        var result = await _controller.Stop(sessionId.ToString());

        var ok = Assert.IsType<OkObjectResult>(result);
        await _streams.Received(1).StopAsync(sessionId);
    }

    [Fact]
    public async Task Stop_IgnoresInvalidStreamId()
    {
        SetupUser();

        var result = await _controller.Stop("bad-guid");

        Assert.IsType<OkObjectResult>(result);
        await _streams.DidNotReceive().StopAsync(Arg.Any<Guid>());
    }
}
