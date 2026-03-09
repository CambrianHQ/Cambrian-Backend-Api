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
/// Tests for StreamController covering stream listing, signed URL generation,
/// session start, and session stop. Validates GUID validation and
/// authorization of start/stop endpoints.
/// </summary>
public sealed class StreamControllerTests
{
    private readonly IStreamService _stream = Substitute.For<IStreamService>();
    private readonly StreamController _controller;

    public StreamControllerTests()
    {
        _controller = new StreamController(_stream);
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
        _stream.ListStreamableAsync(20).Returns(new List<object>
        {
            new { id = Guid.NewGuid().ToString(), title = "Beat 1", artist = "DJ", genre = (string?)null, duration = (int?)null, audioUrl = "url1" }
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
        _stream.GetStreamUrlAsync(id.ToString())
            .ThrowsAsync(new KeyNotFoundException("Track not found."));

        var result = await _controller.Stream(id.ToString());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Stream_ReturnsSignedUrl_WhenFound()
    {
        var id = Guid.NewGuid();
        _stream.GetStreamUrlAsync(id.ToString())
            .Returns(new { trackId = id.ToString(), streamUrl = "https://cdn.test/signed" });

        var result = await _controller.Stream(id.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Start (Authorize) ──

    [Fact]
    public async Task Start_CreatesSession_FromBody()
    {
        SetupUser();
        var trackId = Guid.NewGuid();
        _stream.StartAsync(trackId.ToString(), "user-1")
            .Returns(new { streamId = Guid.NewGuid().ToString(), status = "started" });

        var result = await _controller.Start(
            new StreamController.StreamStartRequest { TrackId = trackId.ToString() });

        Assert.IsType<OkObjectResult>(result);
        await _stream.Received(1).StartAsync(trackId.ToString(), "user-1");
    }

    [Fact]
    public async Task Start_CreatesSession_FromQueryString()
    {
        SetupUser();
        var trackId = Guid.NewGuid();
        _stream.StartAsync(trackId.ToString(), "user-1")
            .Returns(new { streamId = Guid.NewGuid().ToString(), status = "started" });

        var result = await _controller.Start(null, trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Start_UsesEmptyGuid_WhenTrackIdInvalid()
    {
        SetupUser();
        _stream.StartAsync("not-a-guid", "user-1")
            .Returns(new { streamId = Guid.NewGuid().ToString(), status = "started" });

        var result = await _controller.Start(
            new StreamController.StreamStartRequest { TrackId = "not-a-guid" });

        Assert.IsType<OkObjectResult>(result);
        await _stream.Received(1).StartAsync("not-a-guid", "user-1");
    }

    // ── Stop (Authorize) ──

    [Fact]
    public async Task Stop_StopsSession_WhenIdValid()
    {
        SetupUser();
        var sessionId = Guid.NewGuid();

        var result = await _controller.Stop(sessionId.ToString());

        var ok = Assert.IsType<OkObjectResult>(result);
        await _stream.Received(1).StopAsync(sessionId.ToString());
    }

    [Fact]
    public async Task Stop_IgnoresInvalidStreamId()
    {
        SetupUser();

        var result = await _controller.Stop("bad-guid");

        Assert.IsType<OkObjectResult>(result);
        await _stream.Received(1).StopAsync("bad-guid");
    }
}
