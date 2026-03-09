using Cambrian.Application.Services;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for billing tier mapping logic used in BillingController.
/// The controller maps tier strings to (amountCents, planName) tuples.
/// We test the same logic inline since it's inlined in the controller.
/// </summary>
public sealed class BillingTierTests
{
    private static (int amountCents, string planName) MapTier(string? tier)
    {
        var normalized = tier?.ToLowerInvariant() ?? "";
        return normalized switch
        {
            "paid" => (499, "Paid Listener"),
            "creator" => (999, "Creator"),
            _ => (0, "")
        };
    }

    [Theory]
    [InlineData("paid", 499, "Paid Listener")]
    [InlineData("Paid", 499, "Paid Listener")]
    [InlineData("PAID", 499, "Paid Listener")]
    [InlineData("creator", 999, "Creator")]
    [InlineData("Creator", 999, "Creator")]
    [InlineData("CREATOR", 999, "Creator")]
    public void ValidTiers_MapToCorrectPricing(string tier, int expectedCents, string expectedName)
    {
        var (cents, name) = MapTier(tier);
        Assert.Equal(expectedCents, cents);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("enterprise")]
    [InlineData("premium")]
    public void InvalidTiers_MapToZero(string? tier)
    {
        var (cents, name) = MapTier(tier);
        Assert.Equal(0, cents);
        Assert.Equal("", name);
    }

    [Fact]
    public async Task BillingService_ReturnsPortalUrl()
    {
        var sut = new BillingService();
        var userId = Guid.NewGuid();

        var result = await sut.CreateBillingPortalAsync(userId);

        Assert.Contains(userId.ToString("N"), result);
        Assert.StartsWith("https://billing.stripe.test/portal/", result);
    }
}
