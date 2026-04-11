using System.Security.Claims;
using Cambrian.Api.Controllers;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class CreatorControllerTests
{
    private readonly ICreatorService _creator = Substitute.For<ICreatorService>();
    private readonly ITrackRepository _tracks = Substitute.For<ITrackRepository>();
    private readonly ICreatorIdentityRepository _creators = Substitute.For<ICreatorIdentityRepository>();
    private readonly ICreatorProfileRepository _profiles = Substitute.For<ICreatorProfileRepository>();
    private readonly IUploadService _upload = Substitute.For<IUploadService>();
    private readonly CreatorController _controller;

    public CreatorControllerTests()
    {
        _controller = new CreatorController(_creator, _tracks, _creators, _profiles, _upload);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Role, "Creator")
        ], "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Fact]
    public async Task EditTrack_PersistsTaxonomyFields()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            CreatorId = "user-1",
            Title = "Before",
            Genre = "Legacy"
        });

        var result = await _controller.EditTrack(trackId, new EditTrackRequest
        {
            PrimaryGenre = "Hip-Hop",
            Subgenre = "Drill"
        });

        await _tracks.Received(1).UpdateAsync(Arg.Is<Track>(t =>
            t.PrimaryGenre == "Hip-Hop" &&
            t.Subgenre == "Drill" &&
            t.Genre == "Drill"));
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateTrackCoverArt_ReplacesCoverArt()
    {
        var trackId = Guid.NewGuid();
        var track = new Track
        {
            Id = trackId,
            CreatorId = "user-1",
            Title = "Track"
        };
        _tracks.GetByIdAsync(trackId).Returns(track);
        _upload.UploadCoverArtAsync("user-1", Arg.Any<IFormFile>()).Returns("covers/user-1/new-cover.jpg");

        var result = await _controller.UpdateTrackCoverArt(trackId, new UpdateTrackCoverArtRequest
        {
            CoverArt = MakeFormFile("cover.jpg", "image/jpeg")
        });

        await _tracks.Received(1).UpdateAsync(Arg.Is<Track>(t => t.CoverArtUrl == "covers/user-1/new-cover.jpg"));
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task DeleteTrack_RemovesTrackFromCollectionsBeforeDeleting()
    {
        var trackId = Guid.NewGuid();
        _tracks.GetByIdAsync(trackId).Returns(new Track
        {
            Id = trackId,
            CreatorId = "user-1",
            CambrianTrackId = "CAMB-TRK-DELETE01",
            Title = "Track"
        });
        _profiles.GetCollectionsAsync("user-1").Returns(
        [
            new TrackCollectionDto
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Album One",
                TrackIds = [trackId.ToString(), Guid.NewGuid().ToString()]
            },
            new TrackCollectionDto
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Album Two",
                TrackIds = [Guid.NewGuid().ToString()]
            }
        ]);

        var result = await _controller.DeleteTrack(trackId);

        await _profiles.Received(1).UpdateCollectionAsync(
            Arg.Any<Guid>(),
            "user-1",
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string>(s => !s.Contains(trackId.ToString(), StringComparison.OrdinalIgnoreCase)));
        await _tracks.Received(1).DeleteAsync(trackId);
        Assert.IsType<OkObjectResult>(result);
    }

    private static IFormFile MakeFormFile(string fileName, string contentType)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(fileName);
        file.ContentType.Returns(contentType);
        file.Length.Returns(128);
        file.OpenReadStream().Returns(new MemoryStream(new byte[128]));
        return file;
    }
}
