using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Cambrian.Infrastructure.Security;

public interface IPasswordService
{
    string HashPassword(ApplicationUser user, string password);
    bool VerifyPassword(ApplicationUser user, string hashedPassword, string providedPassword);
}

public sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<ApplicationUser> _hasher = new();

    public string HashPassword(ApplicationUser user, string password) => _hasher.HashPassword(user, password);

    public bool VerifyPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        => _hasher.VerifyHashedPassword(user, hashedPassword, providedPassword) != PasswordVerificationResult.Failed;
}
