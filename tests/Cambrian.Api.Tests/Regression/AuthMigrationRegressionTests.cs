using System.Net;
using System.Net.Http.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Regression;

[Trait("Category", "Critical")]
public sealed class AuthMigrationRegressionTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public AuthMigrationRegressionTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FreshRelationalDatabase_AllowsUserRegistration()
    {
        var email = $"fresh-register-{Guid.NewGuid():N}@cambrian.com";
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            password = "Test1234!@",
            displayName = "Fresh Register"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);

        Assert.Equal(0, user.PasswordResetAttemptCount);
        Assert.Null(user.PasswordResetLockedUntil);
    }

    [Fact]
    public async Task ForgotPassword_PersistsAttemptTrackingDefaults()
    {
        var email = $"reset-persist-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);

        var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/forgot-password", new
        {
            email
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == email);

        Assert.False(string.IsNullOrWhiteSpace(user.PasswordResetCode));
        Assert.True(user.PasswordResetCodeExpiry > DateTime.UtcNow);
        Assert.Equal(0, user.PasswordResetAttemptCount);
        Assert.Null(user.PasswordResetLockedUntil);
    }
}
