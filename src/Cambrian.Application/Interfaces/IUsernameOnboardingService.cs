namespace Cambrian.Application.Interfaces;

/// <summary>
/// Outcome of an attempt to complete username onboarding for an account. Mirrors the
/// structured-error style already used by IMediaReadinessService / IPlaybackAccessService.
/// </summary>
public sealed record UsernameOnboardingResult(
    bool Success,
    string? FailureCode = null,
    string? SafeMessage = null,
    string? Username = null,
    string? DisplayName = null,
    string? Role = null)
{
    public static UsernameOnboardingResult Ok(string username, string? displayName, string? role) =>
        new(true, Username: username, DisplayName: displayName, Role: role);

    public static UsernameOnboardingResult Failure(string code, string message) =>
        new(false, FailureCode: code, SafeMessage: message);
}

/// <summary>
/// The single, shared business logic for assigning a username to an account —
/// validation, normalization, reserved-word rejection, dual uniqueness check
/// (Identity + Creators table), the Identity write, and — for Creator/Admin-role
/// accounts — provisioning the Creator row and auto-provisioning CreatorProfile.
/// All writes happen inside one transaction so concurrent callers can never both
/// pass the uniqueness checks and commit duplicate usernames.
///
/// Used by both POST /auth/set-username (self-service, first-time only) and
/// POST /admin/users/{id}/set-username (admin repair for an account stuck by a
/// client/server onboarding-state desync). Do not reimplement this logic at either
/// call site — extend it here so both stay in sync.
/// </summary>
public interface IUsernameOnboardingService
{
    Task<UsernameOnboardingResult> CompleteAsync(string userId, string? requestedUsername, CancellationToken ct = default);
}
