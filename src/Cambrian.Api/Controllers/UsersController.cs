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
    private readonly ICreatorProfileRepository _profiles;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ITransactionManager _transactions;

    public UsersController(
        UserManager<Cambrian.Domain.Entities.ApplicationUser> users,
        ITrackRepository tracks,
        ICreatorProfileRepository profiles,
        ICreatorIdentityRepository creators,
        ITransactionManager transactions)
    {
        _users = users;
        _tracks = tracks;
        _profiles = profiles;
        _creators = creators;
        _transactions = transactions;
    }

    // ───── GET /users/:username — public profile ─────

    [HttpGet("{username}")]
    public async Task<IActionResult> GetProfile(string username)
    {
        var user = await _users.FindByNameAsync(username);
        if (user is null) return NotFoundResponse("User not found.");

        // Resolve creator UUID for dual-FK track query
        var creatorUuid = await _creators.GetCreatorIdForUserAsync(user.Id);
        var userTracks = await _tracks.GetStorefrontTracksAsync(user.Id, creatorUuid);

        var trackList = new List<object>();
        foreach (var t in userTracks)
        {
            trackList.Add(new
            {
                id = t.Id,
                title = t.Title,
                genre = t.Genre,
                coverArtUrl = ResolveImageUrl(t.CoverArtUrl),
                nonExclusivePriceCents = t.NonExclusivePriceCents,
                exclusivePriceCents = t.ExclusivePriceCents,
                copyrightBuyoutPriceCents = t.CopyrightBuyoutPriceCents,
                createdAt = t.CreatedAt
            });
        }

        // Prefer CreatorProfile.Bio (canonical, varchar(2000)), fall back to ApplicationUser.Bio
        var profile = await _profiles.GetByUserIdAsync(user.Id);
        var bio = profile?.Bio ?? user.Bio;

        return OkResponse(new
        {
            username = user.UserName,
            displayName = user.DisplayName,
            profileImageUrl = ResolveImageUrl(user.ProfileImageUrl),
            coverImageUrl = ResolveImageUrl(user.CoverImageUrl),
            bio,
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

        await using var tx = await _transactions.BeginTransactionAsync();

        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var msgs = new List<string>();
            foreach (var e in result.Errors) msgs.Add(e.Description);
            return ErrorResponse(string.Join("; ", msgs));
        }

        // Sync image changes to CreatorProfile (canonical source for marketplace presentation)
        if (body.ProfileImageUrl is not null)
        {
            var url = user.ProfileImageUrl ?? "";
            await _profiles.UpdateImageAsync(userId, null, url);
        }
        if (body.CoverImageUrl is not null)
        {
            var url = user.CoverImageUrl ?? "";
            await _profiles.UpdateImageAsync(userId, url, null);
        }
        // Sync bio to CreatorProfile
        if (body.Bio is not null)
        {
            await _profiles.UpdatePresentationFieldsAsync(userId, user.Bio, null, null, null);
        }

        await _transactions.CommitAsync();

        return OkResponse(new
        {
            username = user.UserName,
            displayName = user.DisplayName,
            profileImageUrl = ResolveImageUrl(user.ProfileImageUrl),
            coverImageUrl = ResolveImageUrl(user.CoverImageUrl),
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
