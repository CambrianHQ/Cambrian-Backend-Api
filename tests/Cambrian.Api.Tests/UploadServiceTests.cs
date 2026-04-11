using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.DTOs.CreatorProfile;
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
    private readonly ICreatorProfileRepository _profiles = Substitute.For<ICreatorProfileRepository>();
    private readonly ICreatorIdentityRepository _creators = Substitute.For<ICreatorIdentityRepository>();
    private readonly UploadService _sut;

    public UploadServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync(Arg.Any<string>()).Returns(new ApplicationUser { Id = "c1", CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free, UploadCount = 0 });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);
        var logger = Substitute.For<ILogger<UploadService>>();
        _sut = new UploadService(_storage, _tracks, users, logger, _creators, _profiles);
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

    [Fact]
    public async Task Upload_PersistsPrimaryGenreAndSubgenre()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            PrimaryGenre = "Electronic",
            Subgenre = "Synthwave"
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.PrimaryGenre == "Electronic" &&
            t.Subgenre == "Synthwave" &&
            t.Genre == "Synthwave"));
    }

    [Fact]
    public async Task Upload_UsesLegacyGenreAsSubgenreAlias()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var request = new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            Genre = "Drill"
        };

        await _sut.Upload(request);

        await _tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            t.Subgenre == "Drill" &&
            t.Genre == "Drill"));
    }

    [Fact]
    public async Task Upload_WithExistingAlbumAssignment_AppendsTrackToCollection()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");
        var collectionId = Guid.NewGuid();
        _profiles.GetCollectionOwnerAsync(collectionId).Returns("c1");
        _profiles.GetCollectionByIdAsync(collectionId).Returns(new TrackCollectionDto
        {
            Id = collectionId.ToString(),
            Title = "Existing Album",
            TrackIds = new[] { "existing-track" }
        });
        _profiles.UpdateCollectionAsync(
                collectionId,
                "c1",
                Arg.Is<string?>(x => x == null),
                Arg.Is<string?>(x => x == null),
                Arg.Is<string?>(x => x == null),
                Arg.Any<string>())
            .Returns(ci => new TrackCollectionDto
            {
                Id = collectionId.ToString(),
                Title = "Existing Album",
                TrackIds = ci.ArgAt<string>(5).Split(',', StringSplitOptions.RemoveEmptyEntries)
            });

        var response = await _sut.Upload(new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            AlbumAssignmentType = "existing",
            CollectionId = collectionId
        });

        Assert.Equal(collectionId.ToString(), response.CollectionId);
        Assert.Contains(response.TrackId, response.CollectionTrackIds);
        await _profiles.Received(1).UpdateCollectionAsync(
            collectionId,
            "c1",
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string?>(x => x == null),
            Arg.Is<string>(s => s.Contains("existing-track") && s.Contains(response.TrackId)));
    }

    [Fact]
    public async Task Upload_WithNewAlbumAssignment_CreatesCollection()
    {
        _storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");
        var collectionId = Guid.NewGuid();
        _profiles.AddCollectionAsync("c1", "Debut Album", "Album notes", Arg.Is<string?>(x => x == null), Arg.Any<string>())
            .Returns(ci => new TrackCollectionDto
            {
                Id = collectionId.ToString(),
                Title = ci.ArgAt<string>(1),
                Description = ci.ArgAt<string>(2),
                TrackIds = ci.ArgAt<string>(4).Split(',', StringSplitOptions.RemoveEmptyEntries)
            });

        var response = await _sut.Upload(new UploadTrackRequest
        {
            Audio = MakeFile(),
            CreatorId = "c1",
            Title = "Beat",
            AlbumAssignmentType = "new",
            NewAlbumTitle = "Debut Album",
            NewAlbumDescription = "Album notes"
        });

        Assert.Equal(collectionId.ToString(), response.CollectionId);
        Assert.Equal("Debut Album", response.CollectionTitle);
        Assert.Contains(response.TrackId, response.CollectionTrackIds);
        await _profiles.Received(1).AddCollectionAsync(
            "c1",
            "Debut Album",
            "Album notes",
            Arg.Is<string?>(x => x == null),
            Arg.Is<string>(s => s == response.TrackId));
    }
}
