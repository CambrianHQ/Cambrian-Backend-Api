using System.Text.RegularExpressions;
using Cambrian.Application.Common;
using Cambrian.Application.DTOs.Creators;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Validation;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cambrian.Persistence.Services;

public sealed class UsernameOnboardingService : IUsernameOnboardingService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ICreatorIdentityRepository _creators;
    private readonly ICreatorProfileRepository _profiles;
    private readonly ITransactionManager _tx;
    private readonly ILogger<UsernameOnboardingService> _logger;

    public UsernameOnboardingService(
        UserManager<ApplicationUser> users,
        ICreatorIdentityRepository creators,
        ICreatorProfileRepository profiles,
        ITransactionManager tx,
        ILogger<UsernameOnboardingService> logger)
    {
        _users = users;
        _creators = creators;
        _profiles = profiles;
        _tx = tx;
        _logger = logger;
    }

    public async Task<UsernameOnboardingResult> CompleteAsync(string userId, string? requestedUsername, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestedUsername))
            return UsernameOnboardingResult.Failure("invalid_username", "Username is required.");

        var normalized = requestedUsername.Trim().ToLowerInvariant();

        if (normalized.Length < 3 || normalized.Length > 40)
            return UsernameOnboardingResult.Failure("invalid_username", "Username must be between 3 and 40 characters.");

        if (!Regex.IsMatch(normalized, @"^[a-z0-9_-]+$"))
            return UsernameOnboardingResult.Failure("invalid_username", "Username may only contain letters, numbers, hyphens, and underscores.");

        if (ReservedUsernames.All.Contains(normalized))
            return UsernameOnboardingResult.Failure("reserved_username", "That username is reserved.");

        var user = await _users.FindByIdAsync(userId);
        if (user is null)
            return UsernameOnboardingResult.Failure("user_not_found", "User not found.");

        // Once a creator has chosen a username it is permanent — reject further changes.
        if (UsernameHelper.IsSet(user))
            return UsernameOnboardingResult.Failure("already_set", "Username cannot be changed once set.");

        // Choosing a username is a generic onboarding step available to ANY authenticated
        // account (listeners included) — it must NOT change the user's role. A listener
        // stays a listener; an account becomes a creator only through an explicit path
        // (registration with role=creator, admin promotion, or admin/billing tier upgrade).
        var isCreatorAccount = string.Equals(user.Role, "Creator", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase);

        // Wrap ALL uniqueness checks + writes in a transaction so concurrent requests
        // cannot both pass checks and commit duplicate usernames.
        await using var transaction = await _tx.BeginTransactionAsync();
        try
        {
            var existingByName = await _users.FindByNameAsync(normalized);
            if (existingByName is not null && existingByName.Id != userId)
            {
                await _tx.RollbackAsync();
                return UsernameOnboardingResult.Failure("username_taken", "That username is already taken.");
            }

            var takenInCreators = await _creators.IsUsernameTakenAsync(normalized);
            if (takenInCreators)
            {
                await _tx.RollbackAsync();
                return UsernameOnboardingResult.Failure("username_taken", "That username is already taken.");
            }

            user.UserName = normalized;
            user.NormalizedUserName = normalized.ToUpperInvariant();
            var displayName = user.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = MetadataSanitizer.NormalizeRequired(requestedUsername, "Display name");
                user.DisplayName = displayName;
            }

            var result = await _users.UpdateAsync(user);
            if (!result.Succeeded)
            {
                await _tx.RollbackAsync();
                var errors = string.Join("; ", result.Errors.Select(e => e.Description));
                _logger.LogWarning("EVENT: SetUsernameFailed userId:{UserId} errors:{Errors}", userId, errors);
                return UsernameOnboardingResult.Failure("identity_update_failed", errors);
            }

            _logger.LogInformation("EVENT: UsernameSet userId:{UserId} username:{Username}", userId, normalized);

            // Creator artifacts (the Creators row that powers /creator/username/{slug} and
            // the public storefront profile) are only provisioned for accounts that are
            // actually creators — provisioning them for a listener would make the listener
            // publicly discoverable as a creator even though no public creator lookup filters
            // on role.
            if (isCreatorAccount)
            {
                await _creators.UpsertAsync(userId, new UpdateCreatorProfileRequest
                {
                    Username = normalized,
                    DisplayName = displayName
                });
                _logger.LogInformation("EVENT: CreatorUsernameSynced userId:{UserId} username:{Username}", userId, normalized);

                // Auto-provision CreatorProfile so the storefront, collections, and
                // /creator/username/{slug} endpoints work immediately without requiring
                // a separate creatorProfileApi.upsert() call.
                try
                {
                    var existingProfile = await _profiles.GetByUserIdAsync(userId);
                    if (existingProfile is null)
                    {
                        await _profiles.UpsertAsync(userId, normalized, "", null, null, false, true);
                        _logger.LogInformation("EVENT: CreatorProfileProvisioned userId:{UserId} slug:{Slug}", userId, normalized);
                    }
                }
                catch (Exception profileEx)
                {
                    // Non-critical — log but don't fail the transaction; the user can
                    // create the storefront profile manually.
                    _logger.LogWarning(profileEx, "CreatorProfile auto-provision failed for userId={UserId}; user can create it manually", userId);
                }
            }

            await _tx.CommitAsync();
            return UsernameOnboardingResult.Ok(normalized, displayName, user.Role);
        }
        catch (DbUpdateException dbEx) when (
            dbEx.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true ||
            dbEx.InnerException?.Message.Contains("23505", StringComparison.Ordinal) == true)
        {
            await _tx.RollbackAsync();
            _logger.LogWarning("EVENT: CreatorUsernameConflict userId:{UserId} username:{Username} — DB unique violation", userId, normalized);
            return UsernameOnboardingResult.Failure("username_taken", "That username is already taken.");
        }
        catch (Exception ex)
        {
            await _tx.RollbackAsync();
            _logger.LogError(ex, "EVENT: SetUsernameFailed userId:{UserId} — rolling back Identity + Creator changes", userId);
            return UsernameOnboardingResult.Failure("unexpected_error", "Failed to complete username setup. Please try again.");
        }
    }
}
