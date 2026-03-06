using System.Security.Claims;
using Cambrian.Application.DTOs.Auth;

namespace Cambrian.Application.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request);

    Task<AuthResponse> RegisterAsync(RegisterRequest request);

    Task<UserProfileResponse> GetCurrentUserAsync(ClaimsPrincipal principal);

    Task ForgotPasswordAsync(ForgotPasswordRequest request);

    Task VerifyCodeAsync(VerifyCodeRequest request);

    Task ResetPasswordAsync(ResetPasswordRequest request);

    Task RecoverUsernameAsync(RecoverUsernameRequest request);
}