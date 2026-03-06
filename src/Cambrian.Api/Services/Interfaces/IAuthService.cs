using Cambrian.Api.DTOs;

namespace Cambrian.Api.Services;

public interface IAuthService
{
    Task<LoginResponse> Register(RegisterRequest request);

    Task<LoginResponse> Login(LoginRequest request);
}
