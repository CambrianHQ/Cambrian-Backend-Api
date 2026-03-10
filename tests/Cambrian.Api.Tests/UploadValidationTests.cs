using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Phase 1 tests: upload file-type and file-size validation.
/// </summary>
public sealed class UploadValidationTests
{
    private readonly IObjectStorage _storage;
    private readonly ITrackRepository _tracks;
    private readonly UploadService _sut;

    public UploadValidationTests()
    {
        _storage = Substitute.For<IObjectStorage>();
        _tracks = Substitute.For<ITrackRepository>();
        _sut = new UploadService(_storage, _tracks);
    }

    private static IFormFile MakeFile(string fileName, string contentType, long sizeBytes = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(fileName);
        file.ContentType.Returns(contentType);
        file.Length.Returns(sizeBytes);
        file.OpenReadStream().Returns(new MemoryStream(new byte[Math.Min(sizeBytes, 64)]));
        return file;
    }

    private static UploadTrackRequest MakeRequest(IFormFile audio, string creatorId = "user-1") =>
        new()
        {
            Audio = audio,
            Title = "Test Track",
            CreatorId = creatorId
        };

    // ── Allowed Extensions ──

    [Theory]
    [InlineData("track.mp3", "audio/mpeg")]
    [InlineData("track.wav", "audio/wav")]
    [InlineData("track.flac", "audio/flac")]
    [InlineData("track.aac", "audio/aac")]
    [InlineData("track.ogg", "audio/ogg")]
    [InlineData("track.m4a", "audio/mp4")]
    public async Task Upload_AcceptsAllowedAudioFormats(string fileName, string contentType)
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.example.com/tracks/" + fileName);

        var result = await _sut.Upload(MakeRequest(MakeFile(fileName, contentType)));

        Assert.NotNull(result);
    }

    // ── Rejected Extensions ──

    [Theory]
    [InlineData("script.exe", "application/octet-stream")]
    [InlineData("doc.pdf", "application/pdf")]
    [InlineData("image.png", "image/png")]
    [InlineData("page.html", "text/html")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("data.json", "application/json")]
    public async Task Upload_RejectsNonAudioExtensions(string fileName, string contentType)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Upload(MakeRequest(MakeFile(fileName, contentType))));
    }

    // ── File Size ──

    [Fact]
    public async Task Upload_RejectsOversizedFile()
    {
        long tooLarge = 101L * 1024 * 1024; // 101 MB
        var file = MakeFile("track.mp3", "audio/mpeg", tooLarge);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Upload(MakeRequest(file)));
    }

    [Fact]
    public async Task Upload_AcceptsFileAtSizeLimit()
    {
        long exactLimit = 100L * 1024 * 1024; // 100 MB
        var file = MakeFile("track.mp3", "audio/mpeg", exactLimit);
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.example.com/tracks/track.mp3");

        var result = await _sut.Upload(MakeRequest(file));
        Assert.NotNull(result);
    }

    // ── MIME Type Validation ──

    [Fact]
    public async Task Upload_RejectsInvalidMimeTypeWithAudioExtension()
    {
        // .mp3 extension but text/html content type
        var file = MakeFile("track.mp3", "text/html");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.Upload(MakeRequest(file)));
    }
}
