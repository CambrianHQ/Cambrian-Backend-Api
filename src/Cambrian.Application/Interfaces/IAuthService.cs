using System.Security.Claims;
using Cambrian.Application.DTOs.Auth;

namespace Cambrian.Application.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request);

    Task<AuthResponse> RegisterAsync(RegisterRequest request);

    Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal);

    Task<AuthResponse> GetSessionAsync(ClaimsPrincipal principal);

    Task ForgotPasswordAsync(ForgotPasswordRequest request);

    Task VerifyCodeAsync(VerifyCodeRequest request);

    Task ResetPasswordAsync(ResetPasswordRequest request);

    Task RecoverUsernameAsync(RecoverUsernameRequest request);

    /// <summary>Change password for the authenticated user (requires current password).</summary>
    Task ChangePasswordAsync(ClaimsPrincipal principal, ChangePasswordRequest request);

    /// <summary>Initiate email change: sends verification code to new email (requires current password).</summary>
    Task ChangeEmailAsync(ClaimsPrincipal principal, ChangeEmailRequest request);

    /// <summary>Generate a fresh JWT for the given userId (includes updated tier/role claims).</summary>
    Task<string?> GenerateFreshTokenAsync(string userId);

    /// <summary>Authenticate via Google ID token (creates account if needed).</summary>
    Task<AuthResponse> GoogleLoginAsync(GoogleLoginRequest request);

    /// <summary>Get the configured Google OAuth Client ID.</summary>
    string GetGoogleClientId();

    /// <summary>
    /// Complete an email change by verifying the token that was sent to the new email address.
    /// </summary>
    Task VerifyEmailChangeAsync(string token);

    /// <summary>
    /// Send (or re-send) an initial email verification link to the authenticated user's
    /// current email address. Idempotent — overwrites any pending token.
    /// </summary>
    Task SendEmailVerificationAsync(ClaimsPrincipal principal);

    /// <summary>
    /// Complete the initial email verification by validating the token from the sent link.
    /// </summary>
    Task VerifyEmailAsync(string token);
}