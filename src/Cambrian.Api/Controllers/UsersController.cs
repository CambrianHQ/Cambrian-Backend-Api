using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("users")]
public class UsersController : BaseController
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ITrackRepository _tracks;
    private readonly IObjectStorage _storage;

    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private const long MaxImageSize = 10 * 1024 * 1024; // 10 MB

    public UsersController(UserManager<ApplicationUser> users, ITrackRepository tracks, IObjectStorage storage)
    {
        _users = users;
        _tracks = tracks;
        _storage = storage;
    }

    // ───── GET /users/:username — public profile ─────

    [HttpGet("{username}")]
    public async Task<IActionResult> GetProfile(string username)
    {
        var user = await _users.FindByNameAsync(username);
        if (user is null) return NotFoundResponse("User not found.");

        var userTracks = await _tracks.GetStorefrontTracksAsync(user.Id);

        return OkResponse(new
        {
            username = user.UserName,
            displayName = user.DisplayName,
            profileImageUrl = user.ProfileImageUrl,
            coverImageUrl = user.CoverImageUrl,
            bio = user.Bio,
            role = user.Role,
            verifiedCreator = user.VerifiedCreator,
            tracks = userTracks.Select(t => new
            {
                id = t.Id,
                title = t.Title,
                genre = t.Genre,
                coverArtUrl = t.CoverArtUrl,
                nonExclusivePriceCents = t.NonExclusivePriceCents,
                exclusivePriceCents = t.ExclusivePriceCents,
                copyrightBuyoutPriceCents = t.CopyrightBuyoutPriceCents,
                createdAt = t.CreatedAt
            })
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
            if (body.Bio.Length > 500)
                return ErrorResponse("Bio must be 500 characters or fewer.");
            user.Bio = body.Bio.Trim();
        }

        if (body.ProfileImageUrl is not null)
            user.ProfileImageUrl = body.ProfileImageUrl;

        if (body.CoverImageUrl is not null)
            user.CoverImageUrl = body.CoverImageUrl;

        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded)
            return ErrorResponse(string.Join("; ", result.Errors.Select(e => e.Description)));

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
