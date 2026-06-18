using System.Reflection;
using Cambrian.Api.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Verifies the money-mutation endpoints carry the tighter "auth" rate-limit policy, while the
/// polled read endpoints (billing status / checkout-confirm) do NOT — so legitimate frontend
/// polling isn't throttled. The "auth" limit is raised to int.MaxValue in Testing, so this asserts
/// the wiring by reflection rather than by exercising the limiter at runtime.
/// </summary>
public sealed class PaymentRateLimitingPolicyTests
{
    [Theory]
    [InlineData(typeof(BillingController), "Checkout")]
    [InlineData(typeof(BillingController), "CheckoutSession")]
    [InlineData(typeof(BillingController), "ApiCheckout")]
    [InlineData(typeof(BillingController), "ApiPortal")]
    [InlineData(typeof(SubscriptionsController), "Update")]
    [InlineData(typeof(SubscriptionsController), "Cancel")]
    [InlineData(typeof(WalletController), "Withdraw")]
    public void MoneyMutationEndpoints_UseAuthRateLimitPolicy(Type controller, string method)
    {
        var m = controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(m);
        var attr = m!.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("auth", attr!.PolicyName);
    }

    [Theory]
    [InlineData(typeof(BillingController), "Status")]      // polled read
    [InlineData(typeof(BillingController), "GetSession")]  // checkout-confirm polling
    public void PolledReads_AreNotAuthRateLimited(Type controller, string method)
    {
        var m = controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(m);
        Assert.Null(m!.GetCustomAttribute<EnableRateLimitingAttribute>());
    }
}
