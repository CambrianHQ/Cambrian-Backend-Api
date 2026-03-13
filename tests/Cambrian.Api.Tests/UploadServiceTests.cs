using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class UploadServiceTests
{
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly UploadService _sut;

    public UploadServiceTests()
    {
        _sut = new UploadService(_storage, _tracks);
    }

    private static IFormFile MakeFile(string name = "beat.mp3", long length = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        file.OpenReadStream().Returns(new MemoryStream(new byte[length]));
        return file;
    }

    [Fact]
    public async Task Upload_ThrowsArgumentException_WhenAudioIsNull()
    {
        var request = new UploadTrackRequest { Audio = null!, CreatorId = "c1", Title = "Beat" };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.Upload(request));
    }

    [Fact]
    public async Task Upload_ThrowsArgumentException_WhenAudioIsEmpty()
    {
        var request = new UploadTrackRequest
        {
            Audio = MakeFile(length: 0),
            CreatorId = "c1",
            Title = "Beat"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.Upload(request));
    }

    [Fact]
    public async Task Upload_ThrowsArgumentException_WhenCreatorIdMissing()
    {
        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "",
            Title = "Beat"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.Upload(request));
    }

    [Fact]
    public async Task Upload_ParsesTags_WithDeduplication()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            Tags = "lo-fi, chill, Lo-Fi, ambient"
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.Tags.Count == 3 &&
            t.Tags.Contains("lo-fi") &&
            t.Tags.Contains("chill") &&
            t.Tags.Contains("ambient")));
    }

    [Fact]
    public async Task Upload_EmptyTags_DefaultsToEmptyList()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            Tags = ""
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t => t.Tags.Count == 0));
    }

    [Fact]
    public async Task Upload_ConvertsPricesToCents()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            NonExclusivePrice = 29.99,
            ExclusivePrice = 499.99
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.NonExclusivePriceCents == 2999 &&
            t.ExclusivePriceCents == 49999));
    }

    [Fact]
    public async Task Upload_DefaultsPriceCentsToZero_WhenNotProvided()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat"
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.NonExclusivePriceCents == 0 &&
            t.ExclusivePriceCents == 0));
    }

    [Fact]
    public async Task Upload_ReturnsNewTrackId()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat"
        };

        var result = await _sut.Upload(request);

        Assert.True(Guid.TryParse(result.TrackId, out _));
        Assert.Equal("Beat", result.Title);
        Assert.NotNull(result.CambrianTrackId);
    }

    [Fact]
    public async Task Upload_SetsAudioUrlFromStorage()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/uploaded-beat.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "My Beat"
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.AudioUrl == "https://cdn.test/uploaded-beat.mp3" &&
            t.Title == "My Beat"));
    }
}
