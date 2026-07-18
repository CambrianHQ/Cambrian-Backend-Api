using System.Security.Claims;
using System.Text.Json;
using Cambrian.Api.Controllers;
using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class StreamControllerTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly IPlaybackTrackingService _playback = Substitute.For<IPlaybackTrackingService>();
    private readonly IPlaybackAccessService _access = Substitute.For<IPlaybackAccessService>();
    private readonly IPlaybackTicketService _tickets = Substitute.For<IPlaybackTicketService>();
    private readonly IMediaProbeSignatureService _probes = Substitute.For<IMediaProbeSignatureService>();
    private readonly StreamController _controller;

    public StreamControllerTests()
    {
        var logger = Substitute.For<ILogger<StreamController>>();
        _controller = new StreamController(
            _tracks,
            _storage,
            new TrackVisibilityPolicy(),
            logger,
            _playback,
            _access,
            _tickets,
            _probes,
            Options.Create(new PlaybackMediaOptions()));
    }

    private void SetAuthenticatedUser(string userId)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.test");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Fact]
    public async Task Start_DelegatesToQualifiedPlaybackService_WithoutCheckingStorage()
    {
        var trackId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        var now = DateTime.SpecifyKind(new DateTime(2026, 7, 13, 12, 0, 0), DateTimeKind.Utc);
        _playback.StartAsync(Arg.Any<PlaybackStartCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PlaybackStartResult(streamId, "started", 30, 60, now, false));
        SetAuthenticatedUser("listener-1");

        var result = await _controller.Start(trackId: trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
        // Start should NOT call storage — audio availability is checked
        // when the client actually streams via GET /stream/{trackId}/audio.
        await _storage.DidNotReceive().OpenReadAsync(Arg.Any<string>());
        await _playback.Received(1).StartAsync(
            Arg.Is<PlaybackStartCommand>(command =>
                command.TrackId == trackId && command.UserId == "listener-1"),
            Arg.Any<CancellationToken>());
        await _tracks.DidNotReceive().GetByIdAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task PublishedTrack_ReturnsApplicationTicketUrl_WithExplicitExpiration()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId, Title = "Playable", AudioUrl = "tracks/creator/playable.mp3",
            Visibility = "public", Status = "available", CreatorId = "creator-1"
        });
        var expires = DateTime.UtcNow.AddMinutes(15);
        _access.PrepareAsync(trackId, "listener-1", false, Arg.Any<CancellationToken>())
            .Returns(new PlaybackAccessResult(PlaybackAccessOutcome.Ready, trackId, "Ready", "audio/mpeg", 1));
        _tickets.Issue(trackId, null).Returns(new PlaybackTicketIssue(
            "ticket-value", "ticket-id", DateTime.UtcNow, expires));
        SetAuthenticatedUser("listener-1");

        var result = Assert.IsType<OkObjectResult>(await _controller.Stream(trackId.ToString()));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data = json.RootElement.GetProperty("data");
        Assert.Equal($"https://api.test/stream/{trackId:D}/audio?ticket=ticket-value", data.GetProperty("streamUrl").GetString());
        Assert.True(data.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow.AddMinutes(14));
        _storage.DidNotReceive().GenerateSignedUrl(Arg.Any<string>());
    }
}
