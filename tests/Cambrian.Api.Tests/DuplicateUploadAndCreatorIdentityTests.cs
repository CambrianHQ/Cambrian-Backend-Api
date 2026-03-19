using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class DuplicateUploadAndCreatorIdentityTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public DuplicateUploadAndCreatorIdentityTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ───────────────────────────────────────────────────────
    //  Unit tests: UploadService duplicate detection
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_DuplicateByHashBlocked_SameCreator()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var tracks = Substitute.For<ITrackRepository>();

        var existingTrack = new Track
        {
            Id = Guid.NewGuid(),
            CambrianTrackId = "CAMB-TRK-EXISTING",
            Title = "Already Here",
            CreatorId = "c1",
            AudioFileHash = "abc123"
        };
        tracks.FindByCreatorAndHashAsync("c1", Arg.Any<string>())
            .Returns(existingTrack);

        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync("c1").Returns(new ApplicationUser
        {
            Id = "c1",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free,
            UploadCount = 0
        });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var logger = Substitute.For<ILogger<UploadService>>();
        var sut = new UploadService(storage, tracks, users, logger);

        var file = MakeFile("beat.mp3", 1024);
        var request = new UploadTrackRequest
        {
            Audio = file,
            CreatorId = "c1",
            Title = "Beat"
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.Upload(request));
        Assert.Contains("already uploaded", ex.Message);
        Assert.Contains("Already Here", ex.Message);
    }

    [Fact]
    public async Task Upload_DifferentCreator_SameHash_Allowed()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var tracks = Substitute.For<ITrackRepository>();
        tracks.FindByCreatorAndHashAsync("c2", Arg.Any<string>())
            .Returns((Track?)null);

        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync("c2").Returns(new ApplicationUser
        {
            Id = "c2",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free,
            UploadCount = 0
        });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var logger = Substitute.For<ILogger<UploadService>>();
        var sut = new UploadService(storage, tracks, users, logger);

        var file = MakeFile("beat.mp3", 1024);
        var request = new UploadTrackRequest
        {
            Audio = file,
            CreatorId = "c2",
            Title = "Beat"
        };

        var result = await sut.Upload(request);
        Assert.NotNull(result.TrackId);
    }

    [Fact]
    public async Task Upload_StoresAudioFileHash()
    {
        var storage = Substitute.For<IObjectStorage>();
        storage.UploadAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://cdn.test/track.mp3");

        var tracks = Substitute.For<ITrackRepository>();
        tracks.FindByCreatorAndHashAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns((Track?)null);

        var store = Substitute.For<IUserStore<ApplicationUser>>();
        var users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);
        users.FindByIdAsync("c3").Returns(new ApplicationUser
        {
            Id = "c3",
            CreatorTier = Cambrian.Domain.Enums.CreatorTier.Free,
            UploadCount = 0
        });
        users.UpdateAsync(Arg.Any<ApplicationUser>()).Returns(IdentityResult.Success);

        var logger = Substitute.For<ILogger<UploadService>>();
        var sut = new UploadService(storage, tracks, users, logger);

        var file = MakeFile("beat.mp3", 1024);
        var request = new UploadTrackRequest
        {
            Audio = file,
            CreatorId = "c3",
            Title = "Hash Test Beat"
        };

        await sut.Upload(request);

        await tracks.Received(1).AddAsync(Arg.Is<Track>(t =>
            !string.IsNullOrEmpty(t.AudioFileHash) &&
            t.AudioFileHash.Length == 64));
    }

    // ───────────────────────────────────────────────────────
    //  Unit tests: ResolveDisplayName (no email leak)
    // ───────────────────────────────────────────────────────

    [Fact]
    public void ResolveDisplayName_PreferDisplayName()
    {
        var user = new ApplicationUser
        {
            DisplayName = "BeatMaker",
            Email = "secret@example.com"
        };
        Assert.Equal("BeatMaker", CatalogService.ResolveDisplayName(user));
    }

    [Fact]
    public void ResolveDisplayName_FallsBackToEmailPrefix_NotFullEmail()
    {
        var user = new ApplicationUser
        {
            DisplayName = null,
            Email = "john.doe@example.com"
        };
        var result = CatalogService.ResolveDisplayName(user);
        Assert.Equal("john.doe", result);
        Assert.DoesNotContain("@", result);
    }

    [Fact]
    public void ResolveDisplayName_NullUser_ReturnsUnknown()
    {
        Assert.Equal("Unknown", CatalogService.ResolveDisplayName(null));
    }

    [Fact]
    public void ResolveDisplayName_EmptyDisplayName_UsesEmailPrefix()
    {
        var user = new ApplicationUser
        {
            DisplayName = "   ",
            Email = "creator@music.com"
        };
        Assert.Equal("creator", CatalogService.ResolveDisplayName(user));
    }

    // ───────────────────────────────────────────────────────
    //  Integration: Auth endpoints include displayName
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task Register_ResponseIncludesDisplayName()
    {
        var email = $"reg-dn-{Guid.NewGuid():N}@test.com";
        var client = _fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Test1234!@",
            displayName = "MyDisplayName"
        });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var user = json.GetProperty("data").GetProperty("user");
        Assert.True(user.TryGetProperty("displayName", out var dn));
        Assert.Equal("MyDisplayName", dn.GetString());
    }

    [Fact]
    public async Task Login_ResponseIncludesDisplayName()
    {
        var email = $"login-dn-{Guid.NewGuid():N}@test.com";
        var password = "Test1234!@";

        var client = _fixture.CreateClient();
        await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password,
            displayName = "LoginUser"
        });

        var loginRes = await client.PostAsJsonAsync("/auth/login", new { email, password });
        loginRes.EnsureSuccessStatusCode();

        var json = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var user = json.GetProperty("data").GetProperty("user");
        Assert.True(user.TryGetProperty("displayName", out var dn));
        Assert.Equal("LoginUser", dn.GetString());
    }

    [Fact]
    public async Task Me_ResponseIncludesDisplayName()
    {
        var email = $"me-dn-{Guid.NewGuid():N}@test.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");

        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var user = json.GetProperty("data").GetProperty("user");
        Assert.True(user.TryGetProperty("displayName", out _));
    }

    // ───────────────────────────────────────────────────────
    //  Integration: TrackResponse includes creator fields
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task TrackResponse_IncludesCreatorFields()
    {
        var email = $"cr-fields-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        await _fixture.SeedTrackAsync(userId, "Creator Fields Beat");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/tracks");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var tracks = json.GetProperty("data");

        var found = false;
        foreach (var t in tracks.EnumerateArray())
        {
            if (t.GetProperty("title").GetString() == "Creator Fields Beat")
            {
                found = true;
                Assert.True(t.TryGetProperty("artist", out var artist));
                Assert.True(t.TryGetProperty("creatorUsername", out _));
                Assert.DoesNotContain("@", artist.GetString() ?? "");
                break;
            }
        }
        Assert.True(found, "Track not found in response");
    }

    [Fact]
    public async Task TrackResponse_NeverLeaksEmail()
    {
        var email = $"no-leak-{Guid.NewGuid():N}@secretdomain.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.DisplayName = null;
            await db.SaveChangesAsync();
        }

        await _fixture.SeedTrackAsync(userId, "No Leak Beat");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/tracks");
        res.EnsureSuccessStatusCode();

        var content = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("@secretdomain.com", content);
    }

    // ───────────────────────────────────────────────────────
    //  Integration: CreatorProfile includes displayName
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreatorProfile_IncludesDisplayName()
    {
        var email = $"cp-dn-{Guid.NewGuid():N}@test.com";
        var client = await CreateCreatorClientAsync(email);

        var slug = $"cpdn-{Guid.NewGuid():N}"[..20];
        var upsertRes = await client.PutAsJsonAsync("/creator-profile/me", new
        {
            slug,
            bio = "Display name test",
            showEarnings = false,
            showDownloadStats = false,
        });
        upsertRes.EnsureSuccessStatusCode();

        var publicClient = _fixture.CreateClient();
        var res = await publicClient.GetAsync($"/creator-profile/{slug}");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.True(data.TryGetProperty("displayName", out var dn));
        Assert.NotNull(dn.GetString());
        Assert.NotEmpty(dn.GetString()!);
    }

    // ───────────────────────────────────────────────────────
    //  Integration: Existing auth/upload still works
    // ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExistingAuth_StillWorks()
    {
        var email = $"auth-ok-{Guid.NewGuid():N}@test.com";
        var token = await _fixture.RegisterUserAsync(email, "Test1234!@");
        Assert.NotNull(token);
        Assert.NotEmpty(token);
    }

    [Fact]
    public async Task OldRecords_WithoutHash_DontBreak()
    {
        var email = $"old-rec-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var id = Guid.NewGuid();
            db.Tracks.Add(new Track
            {
                Id = id,
                CambrianTrackId = $"CAMB-TRK-{id.ToString()[..8].ToUpper()}",
                Title = "Legacy Beat",
                Price = 9.99m,
                LicenseType = "standard",
                AudioUrl = "tracks/legacy.mp3",
                CreatorId = userId,
                Genre = "Hip-Hop",
                AudioFileHash = null
            });
            await db.SaveChangesAsync();
        }

        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/tracks");
        res.EnsureSuccessStatusCode();

        var content = await res.Content.ReadAsStringAsync();
        Assert.Contains("Legacy Beat", content);
    }

    [Fact]
    public async Task UserWithoutProfileImage_StillRendersCorrectly()
    {
        var email = $"no-img-{Guid.NewGuid():N}@test.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        await _fixture.SeedTrackAsync(userId, "No Avatar Beat");

        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/tracks");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var t in json.GetProperty("data").EnumerateArray())
        {
            if (t.GetProperty("title").GetString() == "No Avatar Beat")
            {
                var profileImg = t.GetProperty("creatorProfileImageUrl");
                Assert.True(
                    profileImg.ValueKind == JsonValueKind.Null ||
                    profileImg.ValueKind == JsonValueKind.String);
                break;
            }
        }
    }

    // ───────────────────────────────────────────────────────
    //  Helpers
    // ───────────────────────────────────────────────────────

    private async Task<HttpClient> CreateCreatorClientAsync(string email)
    {
        var password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            user.Tier = "creator";
            await db.SaveChangesAsync();
        }

        var client = _fixture.CreateClient();
        var loginRes = await client.PostAsJsonAsync("/auth/login", new { email, password });
        loginRes.EnsureSuccessStatusCode();
        var json = await loginRes.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static IFormFile MakeFile(string name = "beat.mp3", long length = 1024)
    {
        var file = Substitute.For<IFormFile>();
        file.FileName.Returns(name);
        file.Length.Returns(length);
        file.ContentType.Returns("audio/mpeg");
        file.OpenReadStream().Returns(_ => new MemoryStream(new byte[length]));
        return file;
    }
}
