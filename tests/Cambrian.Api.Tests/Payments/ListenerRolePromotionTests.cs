using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Payments;

/// <summary>
/// REGRESSION (real support case, 2026-07): a listener-role account that
/// bought — or trialed — a creator plan stayed a listener. Capabilities
/// derive from Role, not billing tier, so Stripe billed a subscription that
/// unlocked nothing: Stripe connect and creator profile setup still refused
/// the account, and no self-serve path could fix it.
///
/// The fix: every subscription-activation path calls
/// ApplicationUser.EnsureCreatorRoleForTier() after raising CreatorTier, and
/// migration 20260718203159_PromoteCreatorRoleForPaidTiers repairs accounts
/// already in the broken state.
///
/// Trap that let it ship: real listener signups persist Role = "User" — NOT
/// "Listener" — so role-string checks against "Listener" never fired.
/// </summary>
public sealed class ListenerRolePromotionTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _factory;

    public ListenerRolePromotionTests(CambrianApiFixture factory) => _factory = factory;

    private static string CheckoutPayload(string eventId, string sessionId, string clientRef) => $$"""
    {
        "id": "{{eventId}}",
        "type": "checkout.session.completed",
        "data": {
            "object": {
                "id": "{{sessionId}}",
                "client_reference_id": "{{clientRef}}",
                "customer": "cus_sub_{{sessionId}}",
                "subscription": "sub_{{sessionId}}"
            }
        }
    }
    """;

    private async Task<(string userId, HttpClient client)> NewUserWithRoleAsync(string role)
    {
        var email = $"role-{Guid.NewGuid():N}@cambrian.com";
        await _factory.RegisterUserAsync(email, "Test1234!@");
        var userId = await _factory.GetUserIdAsync(email);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var user = await db.Users.FindAsync(userId);
            user!.Role = role;
            await db.SaveChangesAsync();
        }

        return (userId, _factory.CreateClient());
    }

    private async Task PostWebhookAsync(HttpClient client, string payload)
    {
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/webhook/stripe", content);
        response.EnsureSuccessStatusCode();
    }

    private async Task<ApplicationUser> GetUserAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        return (await db.Users.FindAsync(userId))!;
    }

    [Fact]
    public async Task ListenerRole_CreatorPlanCheckout_Promotes_Role_To_Creator()
    {
        // "User" is what real listener signups persist — the exact broken state.
        var (userId, client) = await NewUserWithRoleAsync("User");
        var sessionId = $"cs_role_{Guid.NewGuid():N}";

        await PostWebhookAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:subscription:creator"));

        var user = await GetUserAsync(userId);
        Assert.Equal("creator", user.Tier);
        Assert.Equal(CreatorTier.Creator, user.CreatorTier);
        Assert.Equal("Creator", user.Role);
    }

    [Fact]
    public async Task AdminRole_CreatorPlanCheckout_Keeps_Admin_Role()
    {
        var (userId, client) = await NewUserWithRoleAsync("Admin");
        var sessionId = $"cs_role_{Guid.NewGuid():N}";

        await PostWebhookAsync(client, CheckoutPayload($"evt_{Guid.NewGuid():N}", sessionId, $"{userId}:subscription:pro"));

        var user = await GetUserAsync(userId);
        Assert.Equal("pro", user.Tier);
        Assert.Equal("Admin", user.Role);
    }

    [Fact]
    public void EnsureCreatorRoleForTier_Promotes_Only_Paid_Creator_Tiers()
    {
        var listenerOnFree = new ApplicationUser { Role = "User", CreatorTier = CreatorTier.Free };
        Assert.False(listenerOnFree.EnsureCreatorRoleForTier());
        Assert.Equal("User", listenerOnFree.Role);

        var listenerOnCreator = new ApplicationUser { Role = "User", CreatorTier = CreatorTier.Creator };
        Assert.True(listenerOnCreator.EnsureCreatorRoleForTier());
        Assert.Equal("Creator", listenerOnCreator.Role);

        var legacyListenerOnPro = new ApplicationUser { Role = "Listener", CreatorTier = CreatorTier.Pro };
        Assert.True(legacyListenerOnPro.EnsureCreatorRoleForTier());
        Assert.Equal("Creator", legacyListenerOnPro.Role);

        var admin = new ApplicationUser { Role = "Admin", CreatorTier = CreatorTier.Pro };
        Assert.False(admin.EnsureCreatorRoleForTier());
        Assert.Equal("Admin", admin.Role);

        var creator = new ApplicationUser { Role = "Creator", CreatorTier = CreatorTier.Creator };
        Assert.False(creator.EnsureCreatorRoleForTier());
        Assert.Equal("Creator", creator.Role);
    }
}
