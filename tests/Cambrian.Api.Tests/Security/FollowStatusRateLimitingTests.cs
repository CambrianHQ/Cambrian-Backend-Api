using System.Reflection;
using Cambrian.Api.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Regression: a page rendering many tracks/creators can call the follow-status endpoint once
/// per card, and this was sharing the same per-user rate-limit bucket as sensitive actions like
/// email verification resend — a follow-status burst could 429 an unrelated action. Both
/// follow-status endpoints must be exempt from rate limiting (the "auth" limit is raised to
/// int.MaxValue in Testing anyway, so this asserts the wiring by reflection, matching
/// PaymentRateLimitingPolicyTests).
/// </summary>
public sealed class FollowStatusRateLimitingTests
{
    [Theory]
    [InlineData(typeof(CreatorProfileController), "GetFollowStatus")]
    [InlineData(typeof(CreatorsController), "GetFollowStatus")]
    public void FollowStatusEndpoints_AreExemptFromRateLimiting(Type controller, string method)
    {
        var m = controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(m);
        Assert.NotNull(m!.GetCustomAttribute<DisableRateLimitingAttribute>());
    }
}
