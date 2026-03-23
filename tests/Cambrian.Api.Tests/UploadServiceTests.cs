using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

[Trait("Category", "Critical")]
public sealed class UploadServiceTests
{
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly UploadService _sut;

    public UploadServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync(Arg.Any<string>()).Returns(new ApplicationUser { Id = "c1", CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free, UploadCount = 0 });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        var logger = Substitute.For<ILogger<UploadService>>();
        var creators = Substitute.For<ICreatorIdentityRepository>();
        _sut = new UploadService(_storage, _tracks, users, logger, creators);
    }

    private static IFormFile MakeFile(string name = "beat.mp3", long length = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        var data = new byte[length];
        WriteMagicBytes(data, Path.GetExtension(name).ToLowerInvariant());
        file.OpenReadStream().Returns(new MemoryStream(data));
        return file;
    }

    private static void WriteMagicBytes(byte[] data, string ext)
    {
        if (data.Length < 2) return;
        switch (ext)
        {
            case ".mp3": data[0] = 0xFF; data[1] = 0xFB; break;
            case ".wav": "RIFF"u8.CopyTo(data); break;
            case ".flac": "fLaC"u8.CopyTo(data); break;
            case ".ogg": "OggS"u8.CopyTo(data); break;
            case ".aac": data[0] = 0xFF; data[1] = 0xF1; break;
            case ".m4a": if (data.Length >= 8) "ftyp"u8.CopyTo(data.AsSpan(4)); break;
        }
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
            NonExclusivePrice = 29.99m,
            ExclusivePrice = 499.99m
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