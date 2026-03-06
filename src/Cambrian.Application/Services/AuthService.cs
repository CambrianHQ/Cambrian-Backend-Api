using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class AuthService : IAuthService
{
    public Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var response = new AuthResponse
        {
            UserId = Guid.NewGuid(),
            Email = request.Email,
            Token = "dev-token"
        };

        return Task.FromResult(response);
    }

    public Task RegisterAsync(RegisterRequest request)
    {
        return Task.CompletedTask;
    }
}
