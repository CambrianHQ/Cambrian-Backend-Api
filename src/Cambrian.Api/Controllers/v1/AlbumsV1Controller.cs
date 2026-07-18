using System.Globalization;
using Cambrian.Api.Middleware;
using Cambrian.Application.DTOs.V1;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Cambrian.Api.Controllers.v1;

/// <summary>
/// Versioned album management for the authenticated creator. Albums are
/// relationships over existing tracks (TrackCollection + AlbumTrack join rows),
/// so adding, removing, or reordering album tracks never mutates, moves, or
/// deletes the Track rows themselves — plays, likes, slugs, and public URLs are
/// preserved. Every action is owner-scoped server-side.
/// JWT bearer only (no API keys): album management is a first-party surface.
/// </summary>
[ApiController]
[Route("api/v1")]
[EnableRateLimiting("auth")]
[Authorize]
public sealed class AlbumsV1Controller : AlbumV1ControllerBase
{
    private readonly ITrackRepository _tracks;

    public AlbumsV1Controller(
        ICreatorProfileRepository profiles,
        ICreatorIdentityRepository creators,
        ICatalogService catalog,
        ITrackVisibilityPolicy trackVisibility,
        ITrackRepository tracks)
        : base(profiles, creators, catalog, trackVisibility)
    {
        _tracks = tracks;
    }

    private const string VisibilityError = "Visibility must be one of: draft, public, unlisted, private.";
    private const string ReleaseDateError = "ReleaseDate must be a valid ISO-8601 date.";

    // ───── List own albums ─────

    /// <summary>List the authenticated creator's albums (all visibilities).</summary>
    [HttpGet("albums")]
    public async Task<IActionResult> ListMyAlbums()
    {
        var userId = GetRequiredUserId()!;
        var collections = await Profiles.GetCollectionsAsync(userId);
        var dtos = new List<AlbumV1Dto>(collections.Count);
        foreach (var c in collections)
            dtos.Add(ToAlbumDto(c, userId));
        return Ok(V1ApiResponse<IReadOnlyList<AlbumV1Dto>>.Ok(dtos));
    }

    // ───── Create ─────

    [HttpPost("albums")]
    [RequireCreatorTier]
    public async Task<IActionResult> CreateAlbum([FromBody] CreateAlbumV1Request body)
    {
        var userId = GetRequiredUserId()!;
        if (string.IsNullOrWhiteSpace(body.Title))
            return BadRequest(V1ApiResponse<object>.Fail("Title is required."));

        var trackCsv = BuildOrderedTrackCsv(body.TrackIds ?? new List<string>());
        if (!await AllTracksOwnedByCreatorAsync(body.TrackIds, userId))
            return BadRequest(V1ApiResponse<object>.Fail("One or more tracks do not belong to you."));

        if (!TryParseVisibility(body.Visibility, out var visibility))
            return BadRequest(V1ApiResponse<object>.Fail(VisibilityError));
        if (!TryParseReleaseDate(body.ReleaseDate, out var releaseDate, out _))
            return BadRequest(V1ApiResponse<object>.Fail(ReleaseDateError));

        var saved = await Profiles.AddCollectionAsync(
            userId, body.Title.Trim(), body.Description?.Trim(), body.ArtworkUrl?.Trim(),
            trackCsv, visibility, releaseDate);
        return StatusCode(201, V1ApiResponse<AlbumV1Dto>.Ok(ToAlbumDto(saved, userId)));
    }

    // ───── Read one (owner detail with hydrated tracks) ─────

    [HttpGet("albums/{id}")]
    [ProducesResponseType(typeof(V1ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlbum(string id)
    {
        var userId = GetRequiredUserId()!;
        if (!Guid.TryParse(id, out var albumId))
            return NotFound(V1ApiResponse<object>.Fail("Album not found."));

        var owner = await Profiles.GetCollectionOwnerAsync(albumId);
        if (owner is null) return NotFound(V1ApiResponse<object>.Fail("Album not found."));
        if (owner != userId) return StatusCode(403, V1ApiResponse<object>.Fail("Access denied."));

        var col = await Profiles.GetCollectionByIdAsync(albumId);
        var detail = await BuildAlbumDetailAsync(col!, owner, requesterId: userId, isAdmin: User.IsInRole("Admin"));
        return Ok(V1ApiResponse<AlbumDetailV1Dto>.Ok(detail));
    }

    // ───── Update (partial) ─────

    [HttpPatch("albums/{id}")]
    [RequireCreatorTier]
    [ProducesResponseType(typeof(V1ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAlbum(string id, [FromBody] UpdateAlbumV1Request body)
    {
        var userId = GetRequiredUserId()!;
        if (!Guid.TryParse(id, out var albumId))
            return NotFound(V1ApiResponse<object>.Fail("Album not found."));

        var owner = await Profiles.GetCollectionOwnerAsync(albumId);
        if (owner is null) return NotFound(V1ApiResponse<object>.Fail("Album not found."));
        if (owner != userId) return StatusCode(403, V1ApiResponse<object>.Fail("Access denied."));

        if (!TryParseVisibility(body.Visibility, out var visibility))
            return BadRequest(V1ApiResponse<object>.Fail(VisibilityError));
        if (!TryParseReleaseDate(body.ReleaseDate, out var releaseDate, out var clearReleaseDate))
            return BadRequest(V1ApiResponse<object>.Fail(ReleaseDateError));

        // trackIds is null here on purpose — album membership is managed via the
        // tracks sub-resource so a metadata PATCH never disturbs the track list.
        var saved = await Profiles.UpdateCollectionAsync(
            albumId, userId, body.Title?.Trim(), body.Description?.Trim(), body.ArtworkUrl?.Trim(),
            trackIds: null, visibility, releaseDate, clearReleaseDate);
        return Ok(V1ApiResponse<AlbumV1Dto>.Ok(ToAlbumDto(saved, userId)));
    }

    // ───── Delete (album only — never the tracks) ─────

    [HttpDelete("albums/{id}")]
    [RequireCreatorTier]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteAlbum(string id)
    {
        var userId = GetRequiredUserId()!;
        if (!Guid.TryParse(id, out var albumId))
            return NotFound(V1ApiResponse<object>.Fail("Album not found."));

        var owner = await Profiles.GetCollectionOwnerAsync(albumId);
        if (owner is null) return NotFound(V1ApiResponse<object>.Fail("Album not found."));
        if (owner != userId) return StatusCode(403, V1ApiResponse<object>.Fail("Access denied."));

        await Profiles.DeleteCollectionAsync(albumId);
        return NoContent();
    }

    // ───── Add tracks (append, preserving order + existing members) ─────

    [HttpPost("albums/{id}/tracks")]
    [RequireCreatorTier]
    public async Task<IActionResult> AddTracks(string id, [FromBody] AddAlbumTracksV1Request body)
    {
        var userId = GetRequiredUserId()!;
        var col = await ResolveOwnedAlbumAsync(id, userId);
        if (col.Error is not null) return col.Error;

        if (body.TrackIds is null || body.TrackIds.Count == 0)
            return BadRequest(V1ApiResponse<object>.Fail("Provide at least one track id."));
        if (!await AllTracksOwnedByCreatorAsync(body.TrackIds, userId))
            return BadRequest(V1ApiResponse<object>.Fail("One or more tracks do not belong to you."));

        var newCsv = BuildOrderedTrackCsv(col.Album!.TrackIds.Concat(body.TrackIds));
        var saved = await Profiles.UpdateCollectionAsync(
            col.AlbumId, userId, null, null, null, trackIds: newCsv);
        return Ok(V1ApiResponse<AlbumV1Dto>.Ok(ToAlbumDto(saved, userId)));
    }

    // ───── Reorder (permutation of current members) ─────

    [HttpPatch("albums/{id}/tracks/reorder")]
    [RequireCreatorTier]
    public async Task<IActionResult> ReorderTracks(string id, [FromBody] ReorderAlbumTracksV1Request body)
    {
        var userId = GetRequiredUserId()!;
        var col = await ResolveOwnedAlbumAsync(id, userId);
        if (col.Error is not null) return col.Error;

        if (body.TrackIds is null)
            return BadRequest(V1ApiResponse<object>.Fail("Provide the album's track ids in the desired order."));

        var current = ParseGuidSet(col.Album!.TrackIds);
        var requested = ParseGuidSet(body.TrackIds);
        if (!current.SetEquals(requested))
            return BadRequest(V1ApiResponse<object>.Fail(
                "Reorder must contain exactly the album's current tracks — add/remove tracks via the tracks endpoints."));

        var newCsv = BuildOrderedTrackCsv(body.TrackIds);
        var saved = await Profiles.UpdateCollectionAsync(
            col.AlbumId, userId, null, null, null, trackIds: newCsv);
        return Ok(V1ApiResponse<AlbumV1Dto>.Ok(ToAlbumDto(saved, userId)));
    }

    // ───── Remove one track (relationship only — the Track row survives) ─────

    [HttpDelete("albums/{id}/tracks/{trackId}")]
    [RequireCreatorTier]
    public async Task<IActionResult> RemoveTrack(string id, string trackId)
    {
        var userId = GetRequiredUserId()!;
        var col = await ResolveOwnedAlbumAsync(id, userId);
        if (col.Error is not null) return col.Error;

        if (!Guid.TryParse(trackId, out var targetTrack))
            return NotFound(V1ApiResponse<object>.Fail("Track is not in this album."));

        var remaining = new List<string>(col.Album!.TrackIds.Count);
        foreach (var t in col.Album.TrackIds)
            if (!(Guid.TryParse(t, out var g) && g == targetTrack))
                remaining.Add(t);
        if (remaining.Count == col.Album.TrackIds.Count)
            return NotFound(V1ApiResponse<object>.Fail("Track is not in this album."));

        var newCsv = BuildOrderedTrackCsv(remaining);
        var saved = await Profiles.UpdateCollectionAsync(
            col.AlbumId, userId, null, null, null, trackIds: newCsv);
        return Ok(V1ApiResponse<AlbumV1Dto>.Ok(ToAlbumDto(saved, userId)));
    }

    // ───── Helpers ─────

    private readonly record struct OwnedAlbum(Guid AlbumId, Application.DTOs.CreatorProfile.TrackCollectionDto? Album, IActionResult? Error);

    /// <summary>Resolve an album and enforce ownership. Returns an Error result to short-circuit on 404/403.</summary>
    private async Task<OwnedAlbum> ResolveOwnedAlbumAsync(string id, string userId)
    {
        if (!Guid.TryParse(id, out var albumId))
            return new OwnedAlbum(Guid.Empty, null, NotFound(V1ApiResponse<object>.Fail("Album not found.")));

        var owner = await Profiles.GetCollectionOwnerAsync(albumId);
        if (owner is null)
            return new OwnedAlbum(albumId, null, NotFound(V1ApiResponse<object>.Fail("Album not found.")));
        if (owner != userId)
            return new OwnedAlbum(albumId, null, StatusCode(403, V1ApiResponse<object>.Fail("Access denied.")));

        var album = await Profiles.GetCollectionByIdAsync(albumId);
        return new OwnedAlbum(albumId, album, null);
    }

    // Returns false if any track id in the list does not belong to userId (or is malformed).
    private async Task<bool> AllTracksOwnedByCreatorAsync(IEnumerable<string>? trackIds, string userId)
    {
        if (trackIds is null) return true;
        var creatorUuid = await Creators.GetCreatorIdForUserAsync(userId);
        foreach (var segment in trackIds)
        {
            if (string.IsNullOrWhiteSpace(segment)) continue;
            if (!Guid.TryParse(segment.Trim(), out var trackId)) return false;
            var track = await _tracks.GetByIdAsync(trackId);
            if (track is null) return false;
            var ownsLegacy = string.Equals(track.CreatorId, userId, StringComparison.Ordinal);
            var ownsUuid = creatorUuid.HasValue && track.CreatorUuid == creatorUuid.Value;
            if (!ownsLegacy && !ownsUuid) return false;
        }
        return true;
    }

    private static HashSet<Guid> ParseGuidSet(IEnumerable<string> ids)
    {
        var set = new HashSet<Guid>();
        foreach (var raw in ids)
            if (Guid.TryParse(raw?.Trim(), out var g)) set.Add(g);
        return set;
    }

    /// <summary>Null keeps the stored date; empty string clears it; otherwise must parse as ISO-8601 (coerced to UTC).</summary>
    private static bool TryParseReleaseDate(string? raw, out DateTime? releaseDate, out bool clear)
    {
        releaseDate = null;
        clear = false;
        if (raw is null) return true;
        if (string.IsNullOrWhiteSpace(raw))
        {
            clear = true;
            return true;
        }
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return false;
        releaseDate = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }
}
