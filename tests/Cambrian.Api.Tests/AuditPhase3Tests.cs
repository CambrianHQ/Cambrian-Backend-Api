using System.Text.Json;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Phase 3A/3B/3C/3D — CreatorProfile is the sole canonical source of truth for presentation
/// fields (bio, profileImageUrl, bannerImageUrl, socialLinks). Creator table
/// stores identity only (username, displayName). No fallback to Creator.
/// </summary>
public sealed class AuditPhase3Tests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly CreatorProfileRepository _profiles;
    private readonly CreatorIdentityRepository _creators;

    public AuditPhase3Tests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
        _profiles = new CreatorProfileRepository(_db);
        _creators = new CreatorIdentityRepository(_db, Substitute.For<ILogger<CreatorIdentityRepository>>());
    }

    public void Dispose() => _db.Dispose();

    private async Task<(string userId, Guid creatorId)> SeedCreatorWithProfile(
        string? creatorBio = "creator-bio",
        string? creatorProfileImage = "creator-img.jpg",
        string? creatorCoverImage = "creator-cover.jpg",
        string? profileBio = null,
        string? profileImage = null,
        string? profileBanner = null)
    {
        var userId = Guid.NewGuid().ToString();
        var username = $"u{Guid.NewGuid():N}"[..12];
        var creatorId = Guid.NewGuid();

        _db.Users.Add(new ApplicationUser
        {
            Id = userId,
            Email = $"{username}@test.com",
            UserName = username,
            NormalizedUserName = username.ToUpperInvariant(),
            NormalizedEmail = $"{username}@test.com".ToUpperInvariant(),
            Role = "Creator",
            Tier = "creator",
            Status = "active",
        });

        _db.Creators.Add(new Creator
        {
            Id = creatorId,
            UserId = userId,
            Username = username,
            DisplayName = username,
            Bio = creatorBio ?? "",
            ProfileImageUrl = creatorProfileImage,
            CoverImageUrl = creatorCoverImage,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        if (profileBio is not null || profileImage is not null || profileBanner is not null)
        {
            _db.CreatorProfiles.Add(new CreatorProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Slug = username,
                Bio = profileBio ?? "",
                ProfileImageUrl = profileImage,
                BannerImageUrl = profileBanner,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync();
        return (userId, creatorId);
    }

    // ════════════════════════════════════════════════════════════════
    // 3A — Write paths: CreatorProfile is the canonical write target
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdatePresentationFields_WritesToCreatorProfile()
    {
        var (userId, _) = await SeedCreatorWithProfile(profileBio: "old-bio");

        var result = await _profiles.UpdatePresentationFieldsAsync(
            userId, "new-bio", null, null, "new-avatar.jpg");

        Assert.NotNull(result);
        Assert.Equal("new-bio", result!.Bio);
        Assert.Equal("new-avatar.jpg", result.ProfileImageUrl);

        // Verify DB state
        var profile = await _db.CreatorProfiles.FirstAsync(p => p.UserId == userId);
        Assert.Equal("new-bio", profile.Bio);
        Assert.Equal("new-avatar.jpg", profile.ProfileImageUrl);
    }

    [Fact]
    public async Task UpdatePresentationFields_ReturnsNull_WhenNoProfileExists()
    {
        var (userId, _) = await SeedCreatorWithProfile(); // No CreatorProfile row

        var result = await _profiles.UpdatePresentationFieldsAsync(
            userId, "bio", null, null, null);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdatePresentationFields_DoesNotOverwriteNullParams()
    {
        var (userId, _) = await SeedCreatorWithProfile(
            profileBio: "keep-this", profileImage: "keep-img.jpg", profileBanner: "keep-banner.jpg");

        // Only update bio, leave images untouched
        await _profiles.UpdatePresentationFieldsAsync(userId, "updated-bio", null, null, null);

        var profile = await _db.CreatorProfiles.FirstAsync(p => p.UserId == userId);
        Assert.Equal("updated-bio", profile.Bio);
        Assert.Equal("keep-img.jpg", profile.ProfileImageUrl);
        Assert.Equal("keep-banner.jpg", profile.BannerImageUrl);
    }

    // ════════════════════════════════════════════════════════════════
    // 3B — Read paths: CreatorProfile canonical, Creator fallback
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MapToDto_PrefersCreatorProfile_OverCreator_ForBio()
    {
        var (_, creatorId) = await SeedCreatorWithProfile(
            creatorBio: "legacy-bio",
            profileBio: "canonical-bio");

        var dto = await _creators.GetByIdAsync(creatorId);

        Assert.NotNull(dto);
        Assert.Equal("canonical-bio", dto!.Bio);
    }

    [Fact]
    public async Task MapToDto_PrefersCreatorProfile_OverCreator_ForImages()
    {
        var (_, creatorId) = await SeedCreatorWithProfile(
            creatorProfileImage: "old-avatar.jpg",
            creatorCoverImage: "old-cover.jpg",
            profileImage: "new-avatar.jpg",
            profileBanner: "new-banner.jpg",
            profileBio: "");

        var dto = await _creators.GetByIdAsync(creatorId);

        Assert.NotNull(dto);
        Assert.Equal("new-avatar.jpg", dto!.ProfileImageUrl);
        Assert.Equal("new-banner.jpg", dto.CoverImageUrl);
    }

    [Fact]
    public async Task MapToDto_ReturnsEmpty_WhenNoCreatorProfile()
    {
        var (_, creatorId) = await SeedCreatorWithProfile(
            creatorBio: "legacy-bio",
            creatorProfileImage: "legacy-img.jpg",
            creatorCoverImage: "legacy-cover.jpg");
        // No CreatorProfile row exists — Creator presentation fields are NOT used

        var dto = await _creators.GetByIdAsync(creatorId);

        Assert.NotNull(dto);
        Assert.Equal("", dto!.Bio);
        Assert.Null(dto.ProfileImageUrl);
        Assert.Null(dto.CoverImageUrl);
    }

    [Fact]
    public async Task MapToDto_ReturnsNull_WhenProfileFieldsNull()
    {
        var (_, creatorId) = await SeedCreatorWithProfile(
            creatorBio: "legacy-bio",
            creatorProfileImage: "legacy-img.jpg",
            creatorCoverImage: "legacy-cover.jpg",
            profileBio: "", // empty string — returned as-is
            profileImage: null, // null — no fallback to Creator
            profileBanner: null); // null — no fallback to Creator

        var dto = await _creators.GetByIdAsync(creatorId);

        Assert.NotNull(dto);
        Assert.Null(dto!.ProfileImageUrl);
        Assert.Null(dto.CoverImageUrl);
    }

    [Fact]
    public async Task MapToDto_PrefersSocialLinks_FromCreatorProfile()
    {
        var (userId, creatorId) = await SeedCreatorWithProfile(profileBio: "");

        // Set social links on Creator table (legacy)
        var creator = await _db.Creators.FirstAsync(c => c.Id == creatorId);
        creator.SocialLinks = JsonSerializer.Serialize(new[] { new SocialLinkItemDto { Platform = "twitter", Url = "https://twitter.com/old" } });
        await _db.SaveChangesAsync();

        // Set social links on CreatorProfile (canonical)
        var profile = await _db.CreatorProfiles.FirstAsync(p => p.UserId == userId);
        profile.SocialLinks = JsonSerializer.Serialize(new[] { new SocialLinkItemDto { Platform = "twitter", Url = "https://twitter.com/new" } });
        await _db.SaveChangesAsync();

        // Detach so repository uses fresh reads
        _db.ChangeTracker.Clear();

        var dto = await _creators.GetByIdAsync(creatorId);
        Assert.NotNull(dto);
        Assert.NotNull(dto!.SocialLinks);
        Assert.Contains(dto.SocialLinks!, l => l.Url == "https://twitter.com/new");
    }

    [Fact]
    public async Task TrackListing_SourcesImageFromCreatorProfile()
    {
        var (userId, creatorId) = await SeedCreatorWithProfile(
            creatorProfileImage: "old-img.jpg",
            profileImage: "new-img.jpg",
            profileBio: "");

        // Seed a track linked via UUID
        _db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            CambrianTrackId = $"CAMB-TRK-{Guid.NewGuid().ToString()[..8].ToUpper()}",
            Title = "Test Track",
            Price = 9.99m,
            CreatorId = userId,
            CreatorUuid = creatorId,
            Genre = "Pop",
            Visibility = "public",
            Status = "available",
        });
        await _db.SaveChangesAsync();

        var tracks = await _creators.GetTracksByCreatorIdAsync(creatorId, 1, 10);

        Assert.Single(tracks);
        Assert.Equal("new-img.jpg", tracks[0].CreatorProfileImageUrl);
    }

    // ════════════════════════════════════════════════════════════════
    // 3A — Verify Creator UpsertAsync no longer writes presentation fields
    // when those fields are null on the request (identity-only update)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpsertAsync_IdentityOnlyRequest_DoesNotOverwriteCreatorPresentationFields()
    {
        var (userId, creatorId) = await SeedCreatorWithProfile(
            creatorBio: "original-bio",
            creatorProfileImage: "original-img.jpg");

        // Simulate the identity-only request pattern used by UpdateMyProfile
        var identityRequest = new UpdateCreatorProfileRequest
        {
            DisplayName = "New Display Name",
        };

        var result = await _creators.UpsertAsync(userId, identityRequest);

        // Creator table should keep its existing bio/images (not nulled)
        var creator = await _db.Creators.FirstAsync(c => c.Id == creatorId);
        Assert.Equal("original-bio", creator.Bio);
        Assert.Equal("original-img.jpg", creator.ProfileImageUrl);
    }
}
