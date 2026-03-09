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
/// session start, and session stop. Validates input delegation to the service
/// layer and correct HTTP responses.
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
            new { id = Guid.NewGuid().ToString(), title = "Beat 1", artist = "DJ" }
        });

        var result = await _controller.List();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Stream by ID ──

    [Fact]
    public async Task Stream_PropagatesArgumentException_WhenTrackIdNotGuid()
    {
        _stream.GetStreamUrlAsync("invalid")
            .ThrowsAsync(new ArgumentException("trackId must be a valid GUID."));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _controller.Stream("invalid"));
    }

    [Fact]
    public async Task Stream_PropagatesKeyNotFound_WhenTrackNotFound()
    {
        var id = Guid.NewGuid();
        _stream.GetStreamUrlAsync(id.ToString())
            .ThrowsAsync(new KeyNotFoundException("Track not found."));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _controller.Stream(id.ToString()));
    }

    [Fact]
    public async Task Stream_ReturnsOk_WhenFound()
    {
        var id = Guid.NewGuid();
        _stream.GetStreamUrlAsync(id.ToString())
            .Returns(new { trackId = id.ToString(), streamUrl = "https://cdn.test/signed" });

        var result = await _controller.Stream(id.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Start (Authorize) ──

    [Fact]
    public async Task Start_DelegatesToService_FromBody()
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
    public async Task Start_DelegatesToService_FromQueryString()
    {
        SetupUser();
        var trackId = Guid.NewGuid();
        _stream.StartAsync(trackId.ToString(), "user-1")
            .Returns(new { streamId = Guid.NewGuid().ToString(), status = "started" });

        var result = await _controller.Start(null, trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Start_PropagatesArgumentException_WhenTrackIdInvalid()
    {
        SetupUser();
        _stream.StartAsync("not-a-guid", "user-1")
            .ThrowsAsync(new ArgumentException("trackId must be a valid GUID."));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _controller.Start(
                new StreamController.StreamStartRequest { TrackId = "not-a-guid" }));
    }

    [Fact]
    public async Task Start_PropagatesKeyNotFound_WhenTrackMissing()
    {
        SetupUser();
        var trackId = Guid.NewGuid();
        _stream.StartAsync(trackId.ToString(), "user-1")
            .ThrowsAsync(new KeyNotFoundException("Track not found."));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _controller.Start(
                new StreamController.StreamStartRequest { TrackId = trackId.ToString() }));
    }

    // ── Stop (Authorize) ──

    [Fact]
    public async Task Stop_DelegatesToService_WhenIdValid()
    {
        SetupUser();
        var sessionId = Guid.NewGuid();
        _stream.StopAsync(sessionId.ToString()).Returns(Task.CompletedTask);

        var result = await _controller.Stop(sessionId.ToString());

        var ok = Assert.IsType<OkObjectResult>(result);
        await _stream.Received(1).StopAsync(sessionId.ToString());
    }

    [Fact]
    public async Task Stop_PropagatesArgumentException_WhenStreamIdInvalid()
    {
        SetupUser();
        _stream.StopAsync("bad-guid")
            .ThrowsAsync(new ArgumentException("streamId must be a valid GUID."));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _controller.Stop("bad-guid"));
    }
}
