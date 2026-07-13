using System.Security.Claims;
using System.Text.Json;
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
        _controller = new StreamController(_tracks, _storage, _streams, new TrackVisibilityPolicy(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), logger);
    }

    private void SetAuthenticatedUser(string userId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Fact]
    public async Task Start_StartsSession_WithoutCheckingStorage()
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
        _streams.StartAsync(trackId, "listener-1").Returns(new StreamSession
        {
            Id = streamId,
            TrackId = trackId,
            UserId = "listener-1"
        });
        SetAuthenticatedUser("listener-1");

        var result = await _controller.Start(trackId: trackId.ToString());

        Assert.IsType<OkObjectResult>(result);
        // Start should NOT call storage — audio availability is checked
        // when the client actually streams via GET /stream/{trackId}/audio.
        await _storage.DidNotReceive().OpenReadAsync(Arg.Any<string>());
        await _streams.Received(1).StartAsync(trackId, "listener-1");
    }

    [Fact]
    public async Task PublishedTrack_ReturnsSignedPlaybackUrl_WithExplicitExpiration()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId, Title = "Playable", AudioUrl = "tracks/creator/playable.mp3",
            Visibility = "public", Status = "available", CreatorId = "creator-1"
        });
        _storage.OpenReadAsync("tracks/creator/playable.mp3").Returns(new StorageFile
        {
            Stream = new MemoryStream(new byte[] { 1 }), ContentType = "audio/mpeg", Length = 1
        });
        _storage.GenerateSignedUrl("tracks/creator/playable.mp3").Returns("https://storage.test/signed");
        _storage.SignedUrlLifetime.Returns(TimeSpan.FromMinutes(15));
        SetAuthenticatedUser("listener-1");

        var result = Assert.IsType<OkObjectResult>(await _controller.Stream(trackId.ToString()));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("https://storage.test/signed", data.GetProperty("streamUrl").GetString());
        Assert.True(data.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow.AddMinutes(14));
    }
}
