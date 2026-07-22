namespace Cambrian.Application.Common;

/// <summary>
/// Single source of truth for usernames that can never be assigned to an account —
/// shared by the live availability check (GET /auth/username-availability) and the
/// actual assignment path (IUsernameOnboardingService) so the two can never drift.
/// </summary>
public static class ReservedUsernames
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "www", "support", "help", "mail", "blog", "app",
        "creator", "cambrian", "marketplace", "verify", "press", "business",
        "developers", "embed", "sync", "pricing", "about"
    };
}
