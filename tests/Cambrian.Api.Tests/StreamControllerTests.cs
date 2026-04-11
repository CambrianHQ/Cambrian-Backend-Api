using System.Security.Claims;
using Cambrian.Api.Controllers;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class StreamControllerTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly IStreamRepository _streams = Substitute.For<IStreamRepository>();
    private readonly StreamController _controller;

    public StreamControllerTests()
    {
        var logger = Substitute.For<ILogger<StreamController>>();
        _controller = new StreamController(_tracks, _storage, _streams, new TrackVisibilityPolicy(), logger);
    }

    private void SetAuthenticatedUser(string userId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Fact]
    public async Task Start_UsesSeedFallbackAudio_WhenAudioUrlIsMissing()
    {
        var trackId = Guid.NewGuid();
        var streamId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-ABC12345",
            Title = "Seeded Beat",
            AudioUrl = null,
            Visibility = "public",
            CreatorId = "creator-1"
        });
        _storage.OpenReadAsync("tracks/demo-abc12345.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }),
            ContentType = "audio/mpeg",
            Length = 4
        });
        _streams.StartAsync(trackId, "listener-1").Returns(new StreamSession
        {
            Id = streamId,
            TrackId = trackId,
            UserId = "listener-1"
        });
        SetAuthenticatedUser("listener-1");

        var result = await _controller.Start(trackId: trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
        await _storage.Received(1).OpenReadAsync("tracks/demo-abc12345.mp3");
        await _streams.Received(1).StartAsync(trackId, "listener-1");
    }

    [Fact]
    public async Task Start_Returns404_WhenNoPlayableAudioExists()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            CambrianTrackId = "CAMB-TRK-DEF67890",
            Title = "Broken Beat",
            AudioUrl = null,
            Visibility = "public",
            CreatorId = "creator-1"
        });
        _storage.OpenReadAsync("tracks/demo-def67890.mp3").Returns((StorageFile?)null);
        SetAuthenticatedUser("listener-1");

        var result = await _controller.Start(trackId: trackId.ToString());

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
        await _streams.DidNotReceive().StartAsync(Arg.Any<Guid>(), Arg.Any<string?>());
    }
}
