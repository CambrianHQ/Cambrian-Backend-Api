using Cambrian.Api.Security;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Albums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Public, versioned album discovery. Anonymous or API-key authenticated.
/// Only publicly-visible albums are ever reachable here: draft/private albums
/// 404 and unlisted albums are excluded from listings. Payloads carry album
/// metadata, a minimal creator summary, and anonymous-safe track projections
/// only — never creator earnings, payment data, drafts, or PII.
/// </summary>
[ApiController]
[Route("api/v1")]
[EnableRateLimiting("api_key_free")]
[AllowApiKey]
[AllowAnonymous]
public sealed class PublicAlbumsV1Controller : AlbumV1ControllerBase
{
    public PublicAlbumsV1Controller(
        ICreatorProfileRepository profiles,
        ICreatorIdentityRepository creators,
        ICatalogService catalog,
        ITrackVisibilityPolicy trackVisibility)
        : base(profiles, creators, catalog, trackVisibility)
    {
    }

    /// <summary>
    /// Public album detail by slug or album id. Resolves only public/unlisted
    /// albums; drafts and private albums 404 for everyone (owners view those via
    /// the authenticated <c>/api/v1/albums/{id}</c>). Hidden tracks inside a
    /// visible album are filtered out of the hydrated track list.
    /// </summary>
    [HttpGet("public/albums/{slug}")]
    public async Task<IActionResult> GetPublicAlbum(string slug)
    {
        Application.DTOs.CreatorProfile.TrackCollectionDto? col = null;

        // A bare GUID is treated as an album id; otherwise it's a slug.
        if (Guid.TryParse(slug, out var albumId))
        {
            var byId = await Profiles.GetCollectionByIdAsync(albumId);
            if (byId is not null && AlbumVisibility.IsPubliclyVisible(byId.Visibility))
                col = byId;
        }

        col ??= await Profiles.GetPublicCollectionBySlugAsync(slug);
        if (col is null)
            return NotFound(V1ApiResponse<object>.Fail("Album not found."));

        var owner = await Profiles.GetCollectionOwnerAsync(Guid.Parse(col.Id));
        // Anonymous public view — requesterId is null so only public tracks hydrate.
        var detail = await BuildAlbumDetailAsync(col, owner ?? "", requesterId: null, isAdmin: false);
        return Ok(V1ApiResponse<AlbumDetailV1Dto>.Ok(detail));
    }

    /// <summary>
    /// List a creator's public albums by username (or UUID/user id). Only
    /// <c>public</c> albums are returned — draft, unlisted, and private albums
    /// never appear in this listing.
    /// </summary>
    [HttpGet("public/creators/{username}/albums")]
    public async Task<IActionResult> GetCreatorAlbums(string username)
    {
        var creator = await Creators.ResolveByLegacyIdentifierAsync(username);
        if (creator is null)
            return NotFound(V1ApiResponse<object>.Fail("Creator not found."));

        var collections = await Profiles.GetCollectionsAsync(creator.UserId);
        var publicAlbums = new List<AlbumV1Dto>();
        foreach (var c in collections)
            if (AlbumVisibility.IsPubliclyListed(c.Visibility))
                publicAlbums.Add(ToAlbumDto(c, creator.UserId));
        return Ok(V1ApiResponse<IReadOnlyList<AlbumV1Dto>>.Ok(publicAlbums));
    }
}
