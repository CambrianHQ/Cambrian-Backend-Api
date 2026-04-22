using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Auth;
using Cambrian.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for CapabilityResolver logic.
/// </summary>
public class CapabilityResolverTests
{
    private readonly IFeatureFlagRepository _flags = Substitute.For<IFeatureFlagRepository>();

    private CapabilityResolver CreateResolver() => new(_flags);

    [Fact]
    public async Task Admin_gets_all_capabilities()
    {
        var user = new ApplicationUser { Role = "Admin" };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.Equal(Capabilities.All.Count, caps.Count);
        Assert.Contains(Capabilities.AdminAccess, caps);
        Assert.Contains(Capabilities.TrackUpload, caps);
        Assert.Contains(Capabilities.PayoutRequest, caps);
    }

    [Fact]
    public async Task Regular_user_gets_license_purchase_only()
    {
        var user = new ApplicationUser
        {
            Role = "User",
            UserName = "test@test.com",
            Email = "test@test.com"
        };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.Contains(Capabilities.LicensePurchase, caps);
        Assert.DoesNotContain(Capabilities.TrackUpload, caps);
        Assert.DoesNotContain(Capabilities.PayoutRequest, caps);
        Assert.DoesNotContain(Capabilities.AdminAccess, caps);
    }

    [Fact]
    public async Task Creator_gets_track_capabilities()
    {
        var user = new ApplicationUser
        {
            Role = "Creator",
            UserName = "beatmaker",
            Email = "beat@test.com"
        };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.Contains(Capabilities.TrackUpload, caps);
        Assert.Contains(Capabilities.TrackEditOwn, caps);
        Assert.Contains(Capabilities.TrackDeleteOwn, caps);
        Assert.Contains(Capabilities.LicensePurchase, caps);
        Assert.DoesNotContain(Capabilities.PayoutRequest, caps);
    }

    [Fact]
    public async Task User_with_username_set_gets_track_capabilities()
    {
        // A user who set a username but Role hasn't been changed yet (edge case)
        var user = new ApplicationUser
        {
            Role = "User",
            UserName = "myusername",
            Email = "different@test.com"
        };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.Contains(Capabilities.TrackUpload, caps);
        Assert.Contains(Capabilities.TrackEditOwn, caps);
    }

    [Fact]
    public async Task Payout_requires_stripe_account_and_feature_flag()
    {
        _flags.IsEnabledAsync("StripeConnectEnabled").Returns(true);

        var user = new ApplicationUser
        {
            Role = "Creator",
            UserName = "creator1",
            Email = "c@test.com",
            StripeAccountId = "acct_test_123"
        };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.Contains(Capabilities.PayoutRequest, caps);
    }

    [Fact]
    public async Task Payout_denied_without_stripe_account()
    {
        _flags.IsEnabledAsync("StripeConnectEnabled").Returns(true);

        var user = new ApplicationUser
        {
            Role = "Creator",
            UserName = "creator1",
            Email = "c@test.com",
            StripeAccountId = null
        };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.DoesNotContain(Capabilities.PayoutRequest, caps);
    }

    [Fact]
    public async Task Payout_denied_when_feature_flag_disabled()
    {
        _flags.IsEnabledAsync("StripeConnectEnabled").Returns(false);

        var user = new ApplicationUser
        {
            Role = "Creator",
            UserName = "creator1",
            Email = "c@test.com",
            StripeAccountId = "acct_test_123"
        };
        var resolver = CreateResolver();

        var caps = await resolver.ResolveAsync(user);

        Assert.DoesNotContain(Capabilities.PayoutRequest, caps);
    }
}

/// <summary>
/// Integration tests for capability-based authorization at the HTTP level.
/// </summary>
public class CapabilityIntegrationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CapabilityIntegrationTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthMe_returns_capabilities_for_creator()
    {
        var client = await _fixture.CreateRoleClientAsync(
            "cap-creator@test.com", "Test1234!@", "Creator", "capcreator");

        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var caps = data.GetProperty("capabilities");

        Assert.Equal(JsonValueKind.Array, caps.ValueKind);

        var capList = caps.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(Capabilities.TrackUpload, capList);
        Assert.Contains(Capabilities.LicensePurchase, capList);
        Assert.Contains(Capabilities.TrackEditOwn, capList);
    }

    [Fact]
    public async Task AuthMe_returns_capabilities_for_regular_user()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "cap-user@test.com", "Test1234!@");

        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var caps = data.GetProperty("capabilities");

        var capList = caps.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(Capabilities.LicensePurchase, capList);
        Assert.DoesNotContain(Capabilities.TrackUpload, capList);
    }

    [Fact]
    public async Task Upload_requires_capability_returns_403_for_user()
    {
        // Regular user (no creator role, username == email) should be forbidden
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "no-upload@test.com", "Test1234!@");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Test Track"), "title");
        form.Add(new ByteArrayContent(new byte[100]), "file", "test.mp3");

        var res = await client.PostAsync("/upload", form);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Payout_capability_not_granted_without_stripe_account()
    {
        // Creator without Stripe account — verify /auth/me does NOT include payout.request
        var client = await _fixture.CreateRoleClientAsync(
            "no-stripe@test.com", "Test1234!@", "Creator", "nostripe");

        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var caps = data.GetProperty("capabilities");
        var capList = caps.EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.DoesNotContain(Capabilities.PayoutRequest, capList);
    }
}
