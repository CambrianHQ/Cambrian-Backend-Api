namespace Cambrian.Api.Security;

public interface IJwtService
{
    string GenerateToken(Guid userId, string email, string role);
}
