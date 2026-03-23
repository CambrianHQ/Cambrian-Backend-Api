using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for cover art upload validation and storage in UploadService.
/// </summary>
public sealed class CoverArtUploadTests
{
    private readonly IObjectStorage _storage = Substitute.For<IObjectStorage>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly UploadService _sut;

    public CoverArtUploadTests()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/file");
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

    private static IFormFile MakeAudioFile(string name = "beat.mp3", long length = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        var data = new byte[Math.Min(length, 1024)];
        WriteMagicBytes(data, Path.GetExtension(name).ToLowerInvariant());
        file.OpenReadStream().Returns(new MemoryStream(data));
        return file;
    }

    private static IFormFile MakeImageFile(
        string name = "cover.jpg",
        string contentType = "image/jpeg",
        long length = 2048)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns(contentType);
        var data = new byte[Math.Min(length, 1024)];
        WriteImageMagicBytes(data, Path.GetExtension(name).ToLowerInvariant());
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

    private static void WriteImageMagicBytes(byte[] data, string ext)
    {
        if (data.Length < 3) return;
        switch (ext)
        {
            case ".jpg" or ".jpeg": data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; break;
            case ".png": if (data.Length >= 4) { data[0] = 0x89; data[1] = 0x50; data[2] = 0x4E; data[3] = 0x47; } break;
            case ".webp": "RIFF"u8.CopyTo(data); break;
        }
    }

    private static UploadTrackRequest MakeRequest(IFormFile audio, IFormFile? coverArt = null) => new()
    {
        Audio = audio,
        CoverArt = coverArt,
        Title = "Test Track",
        CreatorId = "creator-1"
    };

    // ── Happy path ──

    [Fact]
    public async Task Upload_WithCoverArt_StoresCoverArtUrl()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Is<string>(k => k.StartsWith("covers/")), Arg.Any<string>())
            .Returns("https://cdn.test/covers/cover.jpg");

        var request = MakeRequest(MakeAudioFile(), MakeImageFile());

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.CoverArtUrl == "https://cdn.test/covers/cover.jpg"));
    }

    [Fact]
    public async Task Upload_WithoutCoverArt_SetsCoverArtUrlNull()
    {
        var request = MakeRequest(MakeAudioFile());

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.CoverArtUrl == null));
    }

    [Fact]
    public async Task Upload_WithCoverArt_UploadsToCoversPrefix()
    {
        var request = MakeRequest(MakeAudioFile(), MakeImageFile());

        await _sut.Upload(request);

        await _storage.Received(1).UploadAsync(
            Arg.Any<Stream>(),
            Arg.Is<string>(k => k.StartsWith("covers/creator-1/")),
            Arg.Any<string>());
    }

    // ── Accepted image formats ──

    [Theory]
    [InlineData("cover.jpg", "image/jpeg")]
    [InlineData("cover.jpeg", "image/jpeg")]
    [InlineData("cover.png", "image/png")]
    [InlineData("cover.webp", "image/webp")]
    public async Task Upload_Accepts_AllowedImageFormats(string fileName, string contentType)
    {
        var request = MakeRequest(MakeAudioFile(), MakeImageFile(fileName, contentType));

        var result = await _sut.Upload(request);

        Assert.NotNull(result);
        Assert.True(Guid.TryParse(result.TrackId, out _));
    }

    // ── Rejected image formats ──

    [Theory]
    [InlineData("cover.gif", "image/gif")]
    [InlineData("cover.bmp", "image/bmp")]
    [InlineData("cover.svg", "image/svg+xml")]
    [InlineData("cover.exe", "application/x-msdownload")]
    [InlineData("cover.html", "text/html")]
    public async Task Upload_Rejects_NonAllowedImageExtensions(string fileName, string contentType)
    {
        var request = MakeRequest(MakeAudioFile(), MakeImageFile(fileName, contentType));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.Upload(request));

        Assert.Contains("Cover art type", ex.Message);
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public async Task Upload_Rejects_InvalidImageMimeType()
    {
        // .jpg extension but text/html MIME type
        var request = MakeRequest(MakeAudioFile(), MakeImageFile("sneaky.jpg", "text/html"));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.Upload(request));

        Assert.Contains("Cover art MIME type", ex.Message);
        Assert.Contains("not allowed", ex.Message);
    }

    // ── Size limits ──

    [Fact]
    public async Task Upload_Rejects_OversizedCoverArt()
    {
        var bigCover = MakeImageFile("big.jpg", "image/jpeg", 11 * 1024 * 1024); // 11 MB

        var request = MakeRequest(MakeAudioFile(), bigCover);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.Upload(request));

        Assert.Contains("Cover art size", ex.Message);
        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public async Task Upload_Accepts_CoverArtAtSizeLimit()
    {
        var maxCover = MakeImageFile("exact.png", "image/png", 10 * 1024 * 1024); // exactly 10 MB

        var request = MakeRequest(MakeAudioFile(), maxCover);

        var result = await _sut.Upload(request);

        Assert.NotNull(result);
    }

    // ── Empty cover art is treated as no cover ──

    [Fact]
    public async Task Upload_EmptyCoverArt_TreatedAsNull()
    {
        var emptyCover = MakeImageFile(length: 0);

        var request = MakeRequest(MakeAudioFile(), emptyCover);

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.CoverArtUrl == null));
    }

    // ── Cover art uploads use correct content type ──

    [Fact]
    public async Task Upload_CoverArt_UsesCorrectMimeType()
    {
        var request = MakeRequest(MakeAudioFile(), MakeImageFile("cover.png", "image/png"));

        await _sut.Upload(request);

        await _storage.Received(1).UploadAsync(
            Arg.Any<Stream>(),
            Arg.Is<string>(k => k.StartsWith("covers/")),
            "image/png");
    }
}
