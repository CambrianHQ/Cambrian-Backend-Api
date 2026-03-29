using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Cambrian.Domain.Entities;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for UploadService file-type and file-size validation (Phase 1).
/// </summary>
public sealed class UploadValidationTests
{
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly UploadService _sut;

    public UploadValidationTests()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/file.mp3");
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync("creator-1").Returns(new ApplicationUser
        {
            Id = "creator-1",
            UserName = "creator1",
            Email = "creator1@test.com",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free,
            UploadCount = 0
        });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        var logger = Substitute.For<ILogger<UploadService>>();
        var creators = Substitute.For<ICreatorIdentityRepository>();
        _sut = new UploadService(_storage, _tracks, users, logger, creators);
    }

    private static IFormFile MakeFile(string fileName, string contentType, long sizeBytes = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(fileName);
        file.ContentType.Returns(contentType);
        file.Length.Returns(sizeBytes);
        var data = new byte[Math.Min(sizeBytes, 1024)];
        WriteMagicBytes(data, Path.GetExtension(fileName).ToLowerInvariant());
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

    private static UploadTrackRequest MakeRequest(IFormFile audio) => new()
    {
        Audio = audio,
        Title = "Test Track",
        CreatorId = "creator-1"
    };

    [Theory]
    [InlineData("beat.mp3", "audio/mpeg")]
    [InlineData("track.wav", "audio/wav")]
    [InlineData("track.wav", "audio/vnd.wave")]
    [InlineData("track.wav", "audio/x-pn-wav")]
    [InlineData("song.flac", "audio/flac")]
    [InlineData("vibe.aac", "audio/aac")]
    [InlineData("loop.ogg", "audio/ogg")]
    [InlineData("clip.m4a", "audio/m4a")]
    public async Task Upload_Accepts_AllowedAudioFormats(string fileName, string contentType)
    {
        var file = MakeFile(fileName, contentType);
        var result = await _sut.Upload(MakeRequest(file));

        Assert.NotNull(result);
        Assert.True(Guid.TryParse(result.TrackId, out _));
    }

    [Theory]
    [InlineData("malware.exe", "application/x-msdownload")]
    [InlineData("page.html", "text/html")]
    [InlineData("script.js", "application/javascript")]
    [InlineData("image.png", "image/png")]
    [InlineData("doc.pdf", "application/pdf")]
    [InlineData("archive.zip", "application/zip")]
    public async Task Upload_Rejects_NonAudioFileExtensions(string fileName, string contentType)
    {
        var file = MakeFile(fileName, contentType);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Upload(MakeRequest(file)));

        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task Upload_Rejects_OversizedFile()
    {
        var bigFile = MakeFile("huge.mp3", "audio/mpeg", 200 * 1024 * 1024); // 200MB

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Upload(MakeRequest(bigFile)));

        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public async Task Upload_Accepts_FileAtSizeLimit()
    {
        var maxFile = MakeFile("exact.mp3", "audio/mpeg", 100 * 1024 * 1024); // exactly 100MB

        var result = await _sut.Upload(MakeRequest(maxFile));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Upload_Rejects_InvalidMimeType_WithAudioExtension()
    {
        // .mp3 extension but text/html MIME type
        var file = MakeFile("sneaky.mp3", "text/html");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Upload(MakeRequest(file)));

        Assert.Contains("MIME type", ex.Message);
    }
}
