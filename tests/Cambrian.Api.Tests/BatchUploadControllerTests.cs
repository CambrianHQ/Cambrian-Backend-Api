using System.Security.Claims;
using System.Text.Json;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class BatchUploadControllerTests
{
    private readonly IUploadService _uploads = Substitute.For<IUploadService>();
    private readonly UploadController _controller;

    public BatchUploadControllerTests()
    {
        _controller = new UploadController(
            _uploads,
            Substitute.For<IObjectStorage>(),
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<ILogger<UploadController>>());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "creator-1"),
                    new Claim("email_verified", "true"),
                }, "test"))
            }
        };
    }

    [Fact]
    public async Task UploadBatch_TenValidTracks_ReturnsTenExactSuccessResults()
    {
        _uploads.Upload(Arg.Any<UploadTrackRequest>(), Arg.Any<string?>())
            .Returns(call => new UploadTrackResponse
            {
                TrackId = Guid.NewGuid().ToString(),
                Title = call.ArgAt<UploadTrackRequest>(0).Title,
                CambrianTrackId = "CAMB-TRK-TEST",
            });
        var request = new BatchUploadRequest
        {
            Tracks = Enumerable.Range(1, 10).Select(i => Track($"Track {i}", $"track-{i}.mp3")).ToList()
        };

        var result = Assert.IsType<OkObjectResult>(await _controller.UploadBatch(request));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(10, data.GetProperty("total").GetInt32());
        Assert.Equal(10, data.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, data.GetProperty("failed").GetInt32());
        Assert.Equal(10, data.GetProperty("results").GetArrayLength());
        await _uploads.Received(10).Upload(Arg.Any<UploadTrackRequest>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task UploadBatch_InvalidFile_ReturnsPerFileStructuredError_WithoutHidingSuccesses()
    {
        _uploads.Upload(Arg.Is<UploadTrackRequest>(x => x.Title == "invalid"), Arg.Any<string?>())
            .Returns<Task<UploadTrackResponse>>(_ => throw new ArgumentException("File type '.exe' is not allowed."));
        _uploads.Upload(Arg.Is<UploadTrackRequest>(x => x.Title == "valid"), Arg.Any<string?>())
            .Returns(new UploadTrackResponse { TrackId = Guid.NewGuid().ToString(), Title = "valid", CambrianTrackId = "CAMB-TRK-OK" });

        var result = Assert.IsType<OkObjectResult>(await _controller.UploadBatch(new BatchUploadRequest
        {
            Tracks = new() { Track("valid", "valid.mp3"), Track("invalid", "bad.exe") }
        }));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, data.GetProperty("failed").GetInt32());
        var failed = data.GetProperty("results")[1];
        Assert.False(failed.GetProperty("success").GetBoolean());
        Assert.Equal("bad.exe", failed.GetProperty("fileName").GetString());
        Assert.Equal("invalid_file_type", failed.GetProperty("error").GetProperty("code").GetString());
    }

    private static UploadTrackRequest Track(string title, string fileName)
    {
        var bytes = new byte[] { 0xFF, 0xFB, 0, 0 };
        return new UploadTrackRequest
        {
            Title = title,
            Audio = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "audio", fileName)
            {
                Headers = new HeaderDictionary(), ContentType = "audio/mpeg"
            }
        };
    }
}
