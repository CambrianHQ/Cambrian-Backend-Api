using Cambrian.Api.DTOs;
using Cambrian.Api.Entities;
using Cambrian.Api.Repositories;
using Cambrian.Api.Security;

namespace Cambrian.Api.Services;

public class AuthService : IAuthService
{
    private readonly IUserRepository _users;

    private readonly IJwtService _jwt;

    public AuthService(IUserRepository users, IJwtService jwt)
    {
        _users = users;
        _jwt = jwt;
    }

    public async Task<LoginResponse> Register(RegisterRequest request)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            DisplayName = request.DisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password)
        };

        await _users.Add(user);

        var token = _jwt.GenerateToken(user.Id, user.Email, user.Role);

        return new LoginResponse
        {
            Token = token,
            Email = user.Email,
            Tier = user.Tier
        };
    }

    public async Task<LoginResponse> Login(LoginRequest request)
    {
        var user = await _users.GetByEmail(request.Email);

        if (user == null)
            throw new Exception("Invalid credentials");

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new Exception("Invalid credentials");

        var token = _jwt.GenerateToken(user.Id, user.Email, user.Role);

        return new LoginResponse
        {
            Token = token,
            Email = user.Email,
            Tier = user.Tier
        };
    }
}
