using System.Reflection;
using Cambrian.Api.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Cambrian.Api.Tests.Security;

/// <summary>
/// Regression: CreatorProfileController previously shared the "auth" policy (10/min — sized for
/// login/register brute-force protection) at the class level. A single normal profile-edit session
/// (load profile, upload a banner, upload an avatar, save, list collections) already costs 6-8
/// requests on its own, so a second image re-upload or a quick follow-up save routinely 429'd —
/// reported as "banner upload fails or gets stuck". The controller must use its own, more
/// generous "creatorProfile" policy instead. The "auth" limit is raised to int.MaxValue in
/// Testing, so this asserts the wiring by reflection rather than exercising the limiter at
/// runtime (matches PaymentRateLimitingPolicyTests / FollowStatusRateLimitingTests).
/// </summary>
public sealed class CreatorProfileRateLimitingPolicyTests
{
    [Fact]
    public void CreatorProfileController_UsesItsOwnPolicy_NotTheAuthPolicy()
    {
        var attr = typeof(CreatorProfileController).GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("creatorProfile", attr!.PolicyName);
        Assert.NotEqual("auth", attr.PolicyName);
    }

    [Theory]
    [InlineData("GetMyProfile")]
    [InlineData("UpsertProfile")]
    [InlineData("UploadBanner")]
    [InlineData("UploadAvatar")]
    [InlineData("GetMyCollections")]
    [InlineData("CreateCollection")]
    public void CreatorProfileActions_InheritTheClassPolicy_NotOverriddenToAuth(string method)
    {
        var m = typeof(CreatorProfileController).GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(m);
        // None of these actions declare their own [EnableRateLimiting] — they must fall through
        // to the class-level "creatorProfile" policy, not silently re-opt into "auth".
        var methodAttr = m!.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.Null(methodAttr);
    }
}
