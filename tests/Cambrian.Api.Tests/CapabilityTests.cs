using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Auth;
using Cambrian.Domain.Entities;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Unit tests for CapabilityResolver logic. Mirrors the role × tier matrix
/// documented in src/lib/auth/capabilities.ts on the frontend.
/// </summary>
public class CapabilityResolverTests
{
    private readonly IFeatureFlagRepository _flags = Substitute.For<IFeatureFlagRepository>();

    private CapabilityResolver CreateResolver() => new(_flags);

    [Fact]
    public async Task Listener_free_gets_license_purchase_only()
    {
        var user = new ApplicationUser { Role = "User", CreatorTier = CreatorTier.Free };

        var caps = await CreateResolver().ResolveAsync(user);

        Assert.Equal(new[] { Capabilities.LicensePurchase }, caps);
    }

    [Fact]
    public async Task Listener_pro_does_not_get_creator_or_admin_capabilities()
    {
        // Per spec: tier gates Pro-only capabilities, role gates Creator capabilities.
        // A listener who somehow carries CreatorTier.Pro still cannot upload/edit tracks.
        var user = new ApplicationUser { Role = "User", CreatorTier = CreatorTier.Pro };

        var caps = await CreateResolver().ResolveAsync(user);

        Assert.DoesNotContain(Capabilities.TrackUpload, caps);
        Assert.DoesNotContain(Capabilities.TrackEditOwn, caps);
        Assert.DoesNotContain(Capabilities.CreatorDashboardView, caps);
        Assert.DoesNotContain(Capabilities.AdminAccess, caps);
        // Pro tier does grant payout/invoice/exclusive/buyout even for non-creators —
        // spec drives this purely off tier; gating by role is a separate policy concern.
        Assert.Contains(Capabilities.PayoutRequest, caps);
    }

    [Fact]
    public async Task Creator_free_gets_upload_but_no_pro_capabilities()
    {
        var user = new ApplicationUser { Role = "Creator", CreatorTier = CreatorTier.Free };

        var caps = await CreateResolver().ResolveAsync(user);

        Assert.Contains(Capabilities.LicensePurchase, caps);
        Assert.Contains(Capabilities.TrackUpload, caps);
        Assert.Contains(Capabilities.TrackEditOwn, caps);
        Assert.Contains(Capabilities.TrackDeleteOwn, caps);
        Assert.Contains(Capabilities.CreatorDashboardView, caps);

        Assert.DoesNotContain(Capabilities.PayoutRequest, caps);
        Assert.DoesNotContain(Capabilities.InvoiceDownload, caps);
        Assert.DoesNotContain(Capabilities.TrackLicenseExclusive, caps);
        Assert.DoesNotContain(Capabilities.TrackLicenseBuyout, caps);
        Assert.DoesNotContain(Capabilities.AdminAccess, caps);
    }

    [Fact]
    public async Task Creator_pro_gets_all_creator_and_pro_capabilities()
    {
        var user = new ApplicationUser { Role = "Creator", CreatorTier = CreatorTier.Pro };

        var caps = await CreateResolver().ResolveAsync(user);

        Assert.Contains(Capabilities.TrackUpload, caps);
        Assert.Contains(Capabilities.TrackEditOwn, caps);
        Assert.Contains(Capabilities.TrackDeleteOwn, caps);
        Assert.Contains(Capabilities.CreatorDashboardView, caps);
        Assert.Contains(Capabilities.PayoutRequest, caps);
        Assert.Contains(Capabilities.InvoiceDownload, caps);
        Assert.Contains(Capabilities.TrackLicenseExclusive, caps);
        Assert.Contains(Capabilities.TrackLicenseBuyout, caps);
        Assert.DoesNotContain(Capabilities.AdminAccess, caps);
    }

    [Fact]
    public async Task Admin_gets_all_capabilities_regardless_of_tier()
    {
        var user = new ApplicationUser { Role = "Admin", CreatorTier = CreatorTier.Free };

        var caps = await CreateResolver().ResolveAsync(user);

        foreach (var c in Capabilities.All)
        {
            Assert.Contains(c, caps);
        }
        Assert.Equal(Capabilities.All.Count, caps.Count);
    }

    [Fact]
    public async Task Listener_with_tier_string_pro_is_treated_as_pro()
    {
        // Legacy string tier field must also promote to Pro — covers accounts where
        // CreatorTier is unset but the string "pro" field was written by older flows.
        var user = new ApplicationUser { Role = "User", CreatorTier = CreatorTier.Free, Tier = "pro" };

        var caps = await CreateResolver().ResolveAsync(user);

        Assert.Contains(Capabilities.PayoutRequest, caps);
        Assert.Contains(Capabilities.InvoiceDownload, caps);
    }

    [Fact]
    public async Task Downgrade_from_pro_to_free_removes_pro_capabilities()
    {
        var user = new ApplicationUser { Role = "Creator", CreatorTier = CreatorTier.Pro, Tier = "pro" };
        var resolver = CreateResolver();

        var proCaps = await resolver.ResolveAsync(user);
        Assert.Contains(Capabilities.PayoutRequest, proCaps);
        Assert.Contains(Capabilities.TrackLicenseExclusive, proCaps);

        // Subscription ended / tier downgraded
        user.CreatorTier = CreatorTier.Free;
        user.Tier = "free";

        var freeCaps = await resolver.ResolveAsync(user);
        Assert.DoesNotContain(Capabilities.PayoutRequest, freeCaps);
        Assert.DoesNotContain(Capabilities.InvoiceDownload, freeCaps);
        Assert.DoesNotContain(Capabilities.TrackLicenseExclusive, freeCaps);
        Assert.DoesNotContain(Capabilities.TrackLicenseBuyout, freeCaps);
        // Creator role still grants baseline creator caps.
        Assert.Contains(Capabilities.TrackUpload, freeCaps);
    }
}

/// <summary>
/// Integration tests for capabilities over HTTP.
/// </summary>
public class CapabilityIntegrationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public CapabilityIntegrationTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthMe_anonymous_returns_401_not_empty_capabilities()
    {
        var client = _fixture.CreateClient();

        var res = await client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AuthMe_returns_capabilities_for_creator()
    {
        var client = await _fixture.CreateRoleClientAsync(
            "cap-creator@test.com", "Test1234!@", "Creator", "capcreator");

        var capList = await GetCapabilitiesFromMeAsync(client);

        Assert.Contains(Capabilities.LicensePurchase, capList);
        Assert.Contains(Capabilities.TrackUpload, capList);
        Assert.Contains(Capabilities.TrackEditOwn, capList);
        Assert.Contains(Capabilities.CreatorDashboardView, capList);
        Assert.DoesNotContain(Capabilities.AdminAccess, capList);
    }

    [Fact]
    public async Task AuthMe_listener_gets_license_purchase_only()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "cap-listener@test.com", "Test1234!@");

        var capList = await GetCapabilitiesFromMeAsync(client);

        Assert.Contains(Capabilities.LicensePurchase, capList);
        Assert.DoesNotContain(Capabilities.TrackUpload, capList);
        Assert.DoesNotContain(Capabilities.PayoutRequest, capList);
        Assert.DoesNotContain(Capabilities.AdminAccess, capList);
    }

    [Fact]
    public async Task AuthMe_pro_creator_gets_pro_capabilities()
    {
        var email = "cap-pro@test.com";
        var client = await _fixture.CreateRoleClientAsync(email, "Test1234!@", "Creator", "cappro");

        await SetCreatorTierAsync(email, CreatorTier.Pro);

        var capList = await GetCapabilitiesFromMeAsync(client);

        Assert.Contains(Capabilities.PayoutRequest, capList);
        Assert.Contains(Capabilities.InvoiceDownload, capList);
        Assert.Contains(Capabilities.TrackLicenseExclusive, capList);
        Assert.Contains(Capabilities.TrackLicenseBuyout, capList);
    }

    [Fact]
    public async Task AuthMe_admin_gets_admin_access_regardless_of_tier()
    {
        var email = "cap-admin@test.com";
        var client = await _fixture.CreateRoleClientAsync(email, "Test1234!@", "Admin", "capadmin");

        // Tier deliberately left at Free — admin.access must not depend on tier.
        var capList = await GetCapabilitiesFromMeAsync(client);

        Assert.Contains(Capabilities.AdminAccess, capList);
        Assert.Contains(Capabilities.TrackUpload, capList);
        Assert.Contains(Capabilities.PayoutRequest, capList);
    }

    [Fact]
    public async Task Login_response_includes_capabilities()
    {
        const string email = "cap-login@test.com";
        const string password = "Test1234!@";
        await _fixture.RegisterUserAsync(email, password);

        var client = _fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var caps = data.GetProperty("capabilities");

        Assert.Equal(JsonValueKind.Array, caps.ValueKind);
        var capList = caps.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(Capabilities.LicensePurchase, capList);
    }

    [Fact]
    public async Task Register_response_includes_capabilities()
    {
        var client = _fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/auth/register", new
        {
            email = "cap-register@test.com",
            password = "Test1234!@",
            displayName = "TestUser"
        });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var caps = data.GetProperty("capabilities");

        Assert.Equal(JsonValueKind.Array, caps.ValueKind);
        var capList = caps.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(Capabilities.LicensePurchase, capList);
    }

    [Fact]
    public async Task AuthMe_after_downgrade_does_not_include_pro_capabilities()
    {
        var email = "cap-downgrade@test.com";
        var client = await _fixture.CreateRoleClientAsync(email, "Test1234!@", "Creator", "capdown");

        await SetCreatorTierAsync(email, CreatorTier.Pro);
        var proCaps = await GetCapabilitiesFromMeAsync(client);
        Assert.Contains(Capabilities.PayoutRequest, proCaps);

        await SetCreatorTierAsync(email, CreatorTier.Free);
        var freeCaps = await GetCapabilitiesFromMeAsync(client);

        Assert.DoesNotContain(Capabilities.PayoutRequest, freeCaps);
        Assert.DoesNotContain(Capabilities.InvoiceDownload, freeCaps);
        Assert.DoesNotContain(Capabilities.TrackLicenseExclusive, freeCaps);
        Assert.DoesNotContain(Capabilities.TrackLicenseBuyout, freeCaps);
    }

    [Fact]
    public async Task Upload_requires_capability_returns_403_for_user()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(
            "no-upload@test.com", "Test1234!@");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Test Track"), "title");
        form.Add(new ByteArrayContent(new byte[100]), "file", "test.mp3");

        var res = await client.PostAsync("/upload", form);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    private static async Task<List<string?>> GetCapabilitiesFromMeAsync(HttpClient client)
    {
        var res = await client.GetAsync("/auth/me");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        var caps = data.GetProperty("capabilities");
        return caps.EnumerateArray().Select(e => e.GetString()).ToList();
    }

    private async Task SetCreatorTierAsync(string email, CreatorTier tier)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        user.CreatorTier = tier;
        user.Tier = tier == CreatorTier.Pro ? "pro" : "free";
        await db.SaveChangesAsync();
    }
}
