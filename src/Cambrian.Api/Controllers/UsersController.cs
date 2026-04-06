using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("users")]
public class UsersController : BaseController
{
    private readonly UserManager<Cambrian.Domain.Entities.ApplicationUser> _users;
    private readonly ITrackRepository _tracks;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;

    public UsersController(
        UserManager<Cambrian.Domain.Entities.ApplicationUser> users,
        ITrackRepository tracks,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles)
    {
        _users = users;
        _tracks = tracks;
        _creators = creators;
        _profiles = profiles;
    }

    // ───── GET /users/:username — public profile ─────

    [HttpGet("{username}")]
    public async Task<IActionResult> GetProfile(string username)
    {
        var user = await _users.FindByNameAsync(username);
        if (user is null) return NotFoundResponse("User not found.");

        var userTracks = await _tracks.GetStorefrontTracksAsync(user.Id);

        var trackList = new List<object>();
        foreach (var t in userTracks)
        {
            trackList.Add(new
            {
                id = t.Id,
                title = t.Title,
                genre = t.Genre,
                coverArtUrl = t.CoverArtUrl,
                nonExclusivePriceCents = t.NonExclusivePriceCents,
                exclusivePriceCents = t.ExclusivePriceCents,
                copyrightBuyoutPriceCents = t.CopyrightBuyoutPriceCents,
                createdAt = t.CreatedAt
            });
        }

        return OkResponse(new
        {
            username = user.UserName,
            displayName = user.DisplayName,
            profileImageUrl = user.ProfileImageUrl,
            coverImageUrl = user.CoverImageUrl,
            bio = user.Bio,
            role = user.Role,
            verifiedCreator = user.VerifiedCreator,
            tracks = trackList
        });
    }

    // ───── PATCH /users/me — update own profile fields ─────

    [Authorize]
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateUserProfileRequest body)
    {
        var userId = GetRequiredUserId()!;
        var user = await _users.FindByIdAsync(userId);
        if (user is null) return NotFoundResponse("User not found.");

        if (body.Bio is not null)
        {
            // Empty string explicitly clears the field
            var trimmed = body.Bio.Trim();
            if (trimmed.Length > 500)
                return ErrorResponse("Bio must be 500 characters or fewer.");
            user.Bio = trimmed.Length == 0 ? null : trimmed;
        }

        if (body.ProfileImageUrl is not null)
            // Empty string explicitly clears the field
            user.ProfileImageUrl = body.ProfileImageUrl.Trim().Length == 0 ? null : body.ProfileImageUrl.Trim();

        if (body.CoverImageUrl is not null)
            // Empty string explicitly clears the field
            user.CoverImageUrl = body.CoverImageUrl.Trim().Length == 0 ? null : body.CoverImageUrl.Trim();

        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var msgs = new List<string>();
            foreach (var e in result.Errors) msgs.Add(e.Description);
            return ErrorResponse(string.Join("; ", msgs));
        }

        // Sync image changes to Creator and CreatorProfile tables so marketplace displays them
        if (body.ProfileImageUrl is not null)
        {
            var url = user.ProfileImageUrl ?? "";
            await _creators.UpdateImageUrlAsync(userId, "profile", url);
            await _profiles.UpdateImageAsync(userId, null, url);
        }
        if (body.CoverImageUrl is not null)
        {
            var url = user.CoverImageUrl ?? "";
            await _creators.UpdateImageUrlAsync(userId, "cover", url);
            await _profiles.UpdateImageAsync(userId, url, null);
        }

        return OkResponse(new
        {
            username = user.UserName,
            displayName = user.DisplayName,
            profileImageUrl = user.ProfileImageUrl,
            coverImageUrl = user.CoverImageUrl,
            bio = user.Bio
        });
    }
}

/// <summary>Request body for PATCH /users/me.</summary>
public record UpdateUserProfileRequest(
    string? ProfileImageUrl,
    string? CoverImageUrl,
    string? Bio
);
