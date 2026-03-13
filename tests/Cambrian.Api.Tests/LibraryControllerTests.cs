using System.Security.Claims;
using Cambrian.Api.Common;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cambrian.Api.Tests;

/// <summary>
/// Controller-level tests for LibraryController validating GUID checks,
/// response shapes, delegation to LibraryService, and error propagation.
/// Complements the service-level LibraryTests.
/// </summary>
public sealed class LibraryControllerTests
{
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly LibraryController _controller;

    public LibraryControllerTests()
    {
        _controller = new LibraryController(_library);
    }

    private void SetupUser(string userId = "user-1")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    // ── GetLibrary ──

    [Fact]
    public async Task GetLibrary_ReturnsOk()
    {
        SetupUser();
        _library.GetLibraryAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new List<LibraryItemResponse>());

        var result = await _controller.GetLibrary();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── Save ──

    [Fact]
    public async Task Save_Returns201()
    {
        SetupUser();
        _library.SaveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<LibrarySaveRequest>())
            .Returns(Task.CompletedTask);

        var result = await _controller.Save(new LibrarySaveRequest
        {
            TrackId = Guid.NewGuid().ToString()
        });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, obj.StatusCode);
    }

    [Fact]
    public async Task Save_PropagatesKeyNotFound_WhenTrackMissing()
    {
        SetupUser();
        _library.SaveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<LibrarySaveRequest>())
            .ThrowsAsync(new KeyNotFoundException("Track not found."));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _controller.Save(new LibrarySaveRequest
            {
                TrackId = Guid.NewGuid().ToString()
            }));
    }

    // ── Remove ──

    [Fact]
    public async Task Remove_Returns400_WhenTrackIdNotGuid()
    {
        SetupUser();
        var result = await _controller.Remove("bad-id");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(bad.Value);
        Assert.Contains("GUID", envelope.Error);
    }

    [Fact]
    public async Task Remove_ReturnsOk_WhenValid()
    {
        SetupUser();
        _library.RemoveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        var result = await _controller.Remove(Guid.NewGuid().ToString());

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsType<ApiResponse<object?>>(ok.Value);
        Assert.Contains("removed", envelope.Message);
    }

    // ── AddById ──

    [Fact]
    public async Task AddById_Returns400_WhenTrackIdNotGuid()
    {
        SetupUser();
        var result = await _controller.AddById("not-a-guid");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AddById_Returns201_WhenValid()
    {
        SetupUser();
        _library.SaveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<LibrarySaveRequest>())
            .Returns(Task.CompletedTask);

        var result = await _controller.AddById(Guid.NewGuid().ToString());

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, obj.StatusCode);
    }

    // ── PurchasedIds ──

    [Fact]
    public async Task PurchasedIds_ReturnsOk()
    {
        SetupUser();
        _library.GetPurchasedTrackIdsAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new List<string> { Guid.NewGuid().ToString() });

        var result = await _controller.PurchasedIds();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PurchasedIds_ReturnsEmptyList_WhenNoPurchases()
    {
        SetupUser();
        _library.GetPurchasedTrackIdsAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new List<string>());

        var result = await _controller.PurchasedIds();

        Assert.IsType<OkObjectResult>(result);
    }

    // ── ResolveAbsoluteUrl slash separator regression ──

    [Fact]
    public async Task GetLibrary_AudioUrl_NeverMissesSlash()
    {
        // S3ObjectStorage returns object keys without a leading "/" (e.g. "tracks/abc.mp3").
        // ResolveAbsoluteUrl must insert "/" between host and path,
        // otherwise we produce broken URLs like "onrender.comtracks/abc.mp3".
        SetupUser();
        _library.GetLibraryAsync(Arg.Any<ClaimsPrincipal>())
            .Returns(new List<LibraryItemResponse>
            {
                new()
                {
                    TrackId = Guid.NewGuid().ToString(),
                    Title = "Test",
                    Artist = "Tester",
                    AudioUrl = "tracks/creator/beat.mp3", // no leading /
                }
            });

        // Give the controller a real HTTP context so ResolveAbsoluteUrl uses
        // Request.Scheme + Request.Host instead of returning the path as-is.
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1")
        }, "Test"));
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("cambrian-api-staging.onrender.com");
        _controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = await _controller.GetLibrary();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = ok.Value as ApiResponse<IReadOnlyCollection<LibraryItemResponse>>;
        Assert.NotNull(envelope);
        var item = Assert.Single(envelope!.Data!);
        Assert.StartsWith("https://cambrian-api-staging.onrender.com/", item.AudioUrl);
        Assert.DoesNotContain(".comtracks", item.AudioUrl);
    }
}
