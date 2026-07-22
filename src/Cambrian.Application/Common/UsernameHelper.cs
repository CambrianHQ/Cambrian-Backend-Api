using Cambrian.Domain.Entities;

namespace Cambrian.Application.Common;

/// <summary>
/// Centralised logic for determining whether a user has completed username setup.
/// UserName == Email (the Identity default) is a sentinel value meaning "not yet set".
/// Any code that checks this condition must use this helper to stay consistent.
/// </summary>
public static class UsernameHelper
{
    /// <summary>
    /// Returns true when the user has a real, personalised username.
    /// Returns false when UserName is null, empty, whitespace-only, or still equals
    /// the user's email address (the sentinel value set at registration).
    /// </summary>
    public static bool IsSet(ApplicationUser user) =>
        !string.IsNullOrWhiteSpace(user.UserName)
        && !string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the username if set, or null if the user has not completed onboarding.
    /// </summary>
    public static string? GetOrNull(ApplicationUser user) =>
        IsSet(user) ? user.UserName : null;
}
