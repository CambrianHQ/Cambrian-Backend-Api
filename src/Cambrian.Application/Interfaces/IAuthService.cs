using Cambrian.Application.DTOs.Auth;

namespace Cambrian.Application.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request);

    Task RegisterAsync(RegisterRequest request);
}