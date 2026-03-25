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

    /// <summary>Change email for the authenticated user (requires current password).</summary>
    Task ChangeEmailAsync(ClaimsPrincipal principal, ChangeEmailRequest request);

    /// <summary>Generate a fresh JWT for the given userId (includes updated tier/role claims).</summary>
    Task<string?> GenerateFreshTokenAsync(string userId);

    /// <summary>Authenticate via Google ID token (creates account if needed).</summary>
    Task<AuthResponse> GoogleLoginAsync(GoogleLoginRequest request);

    /// <summary>Get the configured Google OAuth Client ID.</summary>
    string GetGoogleClientId();
}