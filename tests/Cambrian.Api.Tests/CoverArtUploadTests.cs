using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
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
        _sut = new UploadService(_storage, _tracks);
    }

    private static IFormFile MakeAudioFile(string name = "beat.mp3", long length = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        file.OpenReadStream().Returns(new MemoryStream(new byte[Math.Min(length, 1024)]));
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
        file.OpenReadStream().Returns(new MemoryStream(new byte[Math.Min(length, 1024)]));
        return file;
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

        var trackId = await _sut.Upload(request);

        Assert.NotNull(trackId);
        Assert.True(Guid.TryParse(trackId, out _));
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

        var trackId = await _sut.Upload(request);

        Assert.NotNull(trackId);
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
