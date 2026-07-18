using System.Security.Claims;
using System.Text;
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

/// <summary>
/// Regression tests for the audio-rehydration invariant: a track is playable iff its DB
/// <see cref="Track.AudioUrl"/> storage key resolves to a real object in R2. These lock in
/// the exact stream-endpoint behavior the rehydration pipeline depends on and verifies —
/// the object the broken-playback incident (149/186 tracks 404ing) was caused by.
///
/// Note on "streamAvailable": the Track entity has no separate streamAvailable/processingStatus
/// flag. Playability is fully determined by (AudioUrl set) AND (object exists at that key).
/// So "streamAvailable is false when the object is missing" == "GET /stream/{id}/audio 404s",
/// and "true only after a verified object exists" == "it returns 200/206". These tests assert
/// exactly that mapping.
/// </summary>
public sealed class AudioRehydrationStreamTests
{
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly IPlaybackTrackingService _playback = Substitute.For<IPlaybackTrackingService>();
    private readonly StreamController _controller;

    public AudioRehydrationStreamTests()
    {
        var logger = Substitute.For<ILogger<StreamController>>();
        _controller = new StreamController(_tracks, _storage, new TrackVisibilityPolicy(), logger, _playback);
    }

    private void SetAnonymousContext(string? range = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity()); // anonymous
        ctx.Response.Body = new MemoryStream();
        if (range is not null) ctx.Request.Headers.Range = range;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    private static Track PublicTrack(string? audioUrl, string cambrianId = "CAMB-TRK-REAL0001") => new()
    {
        Id = Guid.NewGuid(),
        CambrianTrackId = cambrianId,
        Title = "Restored Track",
        AudioUrl = audioUrl,
        Visibility = "public",
        CreatorId = "creator-1",
    };

    private static StorageFile SeekableAudio(byte[] payload, string contentType = "audio/mpeg") => new()
    {
        Stream = new MemoryStream(payload, writable: false), // seekable -> ASP.NET File() branch
        ContentType = contentType,
        Length = payload.Length,
        TotalLength = payload.Length,
        IsPartialContent = false,
    };

    [Fact]
    public async Task StreamAudio_ObjectMissing_ReturnsServiceUnavailable_NotPlaceholder()
    {
        var track = PublicTrack("tracks/96cd73d1/return-to-disco.wav");
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.OpenReadAsync(Arg.Any<string>()).Returns((StorageFile?)null); // nothing in R2
        SetAnonymousContext();

        var result = await _controller.StreamAudio(track.Id.ToString());

        // A validated row whose object disappeared is a dependency failure, never a placeholder.
        var missing = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, missing.StatusCode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(missing.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("media_object_missing", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StreamAudio_DbKeyResolvesToStorageObject()
    {
        const string key = "tracks/96cd73d1/return-to-disco.wav";
        var track = PublicTrack(key);
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.OpenReadAsync(Arg.Any<string>()).Returns((StorageFile?)null);
        SetAnonymousContext();

        await _controller.StreamAudio(track.Id.ToString());

        // The exact DB AudioUrl value is what gets looked up in storage — the contract the
        // rehydration pipeline relies on (upload to the DB key => track resolves).
        await _storage.Received().OpenReadAsync(key);
    }

    [Fact]
    public async Task StreamAudio_ObjectExists_Returns200_WithAudioContentType()
    {
        var payload = Encoding.ASCII.GetBytes("ID3-audio-bytes");
        var track = PublicTrack("tracks/hobo-tracks/abc/original.mp3");
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.OpenReadAsync(track.AudioUrl!).Returns(SeekableAudio(payload, "audio/mpeg"));
        SetAnonymousContext();

        var result = await _controller.StreamAudio(track.Id.ToString());

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.StartsWith("audio/", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task StreamAudio_RangeRequest_ForwardsRangeToStorage()
    {
        const string key = "tracks/hobo-tracks/abc/original.mp3";
        var track = PublicTrack(key);
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.OpenReadAsync(key, "bytes=0-1023")
            .Returns(SeekableAudio(Encoding.ASCII.GetBytes("ABCDEFG")));
        SetAnonymousContext("bytes=0-1023");

        await _controller.StreamAudio(track.Id.ToString());

        // Range requests must reach the origin so it can answer 206 (Safari/iOS require it).
        await _storage.Received().OpenReadAsync(key, "bytes=0-1023");
    }

    [Fact]
    public async Task StreamAudio_UnsatisfiableRange_Preserves416AndTotalLength()
    {
        const string key = "tracks/hobo-tracks/abc/original.mp3";
        var track = PublicTrack(key);
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.OpenReadAsync(key, "bytes=999-1000").Returns(new StorageFile
        {
            Stream = Stream.Null,
            ContentType = "audio/mpeg",
            Length = 0,
            TotalLength = 10,
            IsRangeNotSatisfiable = true,
            StatusCode = 416,
        });
        SetAnonymousContext("bytes=999-1000");

        var result = await _controller.StreamAudio(track.Id.ToString());

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, _controller.Response.StatusCode);
        Assert.Equal("bytes */10", _controller.Response.Headers.ContentRange.ToString());
        Assert.Equal(0, _controller.Response.ContentLength);
    }

    [Fact]
    public async Task StreamAudio_Head_ReturnsMetadataWithoutOpeningBody()
    {
        const string key = "tracks/hobo-tracks/abc/original.mp3";
        var track = PublicTrack(key);
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.GetMetadataAsync(key, Arg.Any<CancellationToken>())
            .Returns(new StorageObjectMetadata(key, 123, "audio/mpeg", null, DateTime.UtcNow));
        SetAnonymousContext();
        _controller.Request.Method = HttpMethods.Head;

        var result = await _controller.StreamAudio(track.Id.ToString());

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(123, _controller.Response.ContentLength);
        Assert.Equal("audio/mpeg", _controller.Response.ContentType);
        await _storage.DidNotReceive().OpenReadAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task StreamAudio_NullKey_NonSeedTrack_ReturnsServiceUnavailable()
    {
        var track = PublicTrack(audioUrl: null);
        _tracks.GetByIdAsync(track.Id).Returns(track);
        _storage.OpenReadAsync(Arg.Any<string>()).Returns((StorageFile?)null);
        SetAnonymousContext();

        var result = await _controller.StreamAudio(track.Id.ToString());

        var missing = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, missing.StatusCode);
    }
}
