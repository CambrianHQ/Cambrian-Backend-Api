using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Cambrian.Api.Tests;

public class CambrianWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestJwtKey = "cambrian-integration-test-key-min-32-chars!!";
    public const string TestIssuer = "cambrian";
    public const string TestAudience = "cambrian";

    public IPaymentGateway MockPaymentGateway { get; } = Substitute.For<IPaymentGateway>();
    public IObjectStorage MockObjectStorage { get; } = Substitute.For<IObjectStorage>();

    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Jwt:Key", TestJwtKey);
        builder.UseSetting("Jwt:Issuer", TestIssuer);
        builder.UseSetting("Jwt:Audience", TestAudience);

        MockObjectStorage.GenerateSignedUrl(Arg.Any<string>())
            .Returns(ci => $"https://storage.test/signed/{ci.Arg<string>()}");
        MockPaymentGateway
            .CreateCheckoutSessionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("https://checkout.stripe.com/test-session");
        MockPaymentGateway
            .CreateSubscriptionCheckoutAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://checkout.stripe.com/test-sub-session");

        builder.ConfigureServices(services =>
        {
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<CambrianDbContext>));
            if (dbDescriptor is not null)
                services.Remove(dbDescriptor);

            services.AddDbContext<CambrianDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            ReplaceService<IPaymentGateway>(services, MockPaymentGateway);
            ReplaceService<IObjectStorage>(services, MockObjectStorage);
        });
    }

    private static void ReplaceService<T>(IServiceCollection services, T instance) where T : class
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
        services.AddSingleton(instance);
    }

    public string GenerateTestJwt(string userId, string email = "test@test.com", string role = "User")
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<string> CreateTestUserAndGetTokenAsync(
        string email = "integration@test.com",
        string password = "StrongP@ss1!")
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = email.Split('@')[0],
            Tier = "free"
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create test user: {string.Join("; ", result.Errors.Select(e => e.Description))}");

        return GenerateTestJwt(user.Id, email);
    }

    public async Task SeedTrackAsync(Guid trackId, string creatorId, string title = "Test Beat",
        double price = 29.99, string audioUrl = "/audio/test.mp3")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        if (!await db.Users.AnyAsync(u => u.Id == creatorId))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = creatorId,
                UserName = $"creator-{creatorId}",
                Email = $"creator-{creatorId}@test.com",
                DisplayName = "Test Creator"
            });
        }

        db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = title,
            Price = price,
            CreatorId = creatorId,
            AudioUrl = audioUrl,
            NonExclusivePriceCents = (int)(price * 100),
            ExclusivePriceCents = (int)(price * 100 * 10),
            Visibility = "public"
        });

        await db.SaveChangesAsync();
    }
}
