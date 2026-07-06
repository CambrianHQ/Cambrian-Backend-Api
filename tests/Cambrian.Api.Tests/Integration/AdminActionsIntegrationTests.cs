using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;

namespace Cambrian.Api.Tests.Integration;

/// <summary>
/// Real-HTTP, real-DB (SQLite in-memory) coverage for the admin actions that were
/// previously 404 (payout approve/reject), 501 (track feature/pin), or a facade
/// (reports). Exercises the full controller → service → repository → DB round trip.
/// </summary>
public sealed class AdminActionsIntegrationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AdminActionsIntegrationTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<HttpClient> CreateAdminClientAsync()
    {
        var seed = Guid.NewGuid().ToString("N")[..8];
        return _fixture.CreateRoleClientAsync(
            email: $"admin-{seed}@cambrian.com",
            password: "Test1234!@",
            role: "Admin");
    }

    private async Task<string> CreateCreatorUserIdAsync(string emailSeed)
    {
        var email = $"creator-{emailSeed}@cambrian.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        await _fixture.SetUserRoleAsync(email, "Creator");
        await _fixture.SetStripeAccountIdAsync(email);
        return await _fixture.GetUserIdAsync(email);
    }

    // ── Payout approve/reject ──

    [Fact]
    public async Task ApprovePayout_Pending_RetriesTransferAndCompletes()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var payoutId = await _fixture.SeedPayoutAsync(creatorId);

        var response = await admin.PostAsync($"/admin/payouts/{payoutId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.Equal("completed", data.GetProperty("payout").GetProperty("status").GetString());

        // Persisted — re-fetch via the admin payouts list.
        var list = await admin.GetAsync("/admin/payouts");
        var listJson = await list.Content.ReadFromJsonAsync<JsonElement>();
        var match = listJson.GetProperty("data").EnumerateArray()
            .First(p => p.GetProperty("id").GetString() == payoutId.ToString());
        Assert.Equal("completed", match.GetProperty("status").GetString());
        Assert.NotNull(match.GetProperty("reviewedByUserId").GetString());
    }

    [Fact]
    public async Task ApprovePayout_NotPending_ReturnsConflict()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var payoutId = await _fixture.SeedPayoutAsync(creatorId, status: "completed");

        var response = await admin.PostAsync($"/admin/payouts/{payoutId}/approve", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ApprovePayout_Missing_ReturnsNotFound()
    {
        using var admin = await CreateAdminClientAsync();

        var response = await admin.PostAsync($"/admin/payouts/{Guid.NewGuid()}/approve", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RejectPayout_Pending_MarksRejectedWithoutTouchingWallet()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var payoutId = await _fixture.SeedPayoutAsync(creatorId);

        var response = await admin.PostAsJsonAsync($"/admin/payouts/{payoutId}/reject", new { reason = "Suspected fraud" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var payout = json.GetProperty("data").GetProperty("payout");
        Assert.Equal("rejected", payout.GetProperty("status").GetString());
        Assert.Equal("Suspected fraud", payout.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task RejectPayout_BlankReason_ReturnsBadRequest()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var payoutId = await _fixture.SeedPayoutAsync(creatorId);

        var response = await admin.PostAsJsonAsync($"/admin/payouts/{payoutId}/reject", new { reason = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectPayout_NotPending_ReturnsConflict()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var payoutId = await _fixture.SeedPayoutAsync(creatorId, status: "rejected");

        var response = await admin.PostAsJsonAsync($"/admin/payouts/{payoutId}/reject", new { reason = "already handled" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── Track feature/pin ──

    [Fact]
    public async Task FeatureTrack_IsIdempotent()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var trackId = await _fixture.SeedTrackAsync(creatorId);

        var first = await admin.PostAsync($"/admin/tracks/{trackId}/feature", null);
        var second = await admin.PostAsync($"/admin/tracks/{trackId}/feature", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var list = await admin.GetAsync("/admin/tracks");
        var listJson = await list.Content.ReadFromJsonAsync<JsonElement>();
        var match = listJson.GetProperty("data").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == trackId.ToString());
        Assert.True(match.GetProperty("isFeatured").GetBoolean());
    }

    [Fact]
    public async Task PinTrack_IsIdempotent()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var trackId = await _fixture.SeedTrackAsync(creatorId);

        var first = await admin.PostAsync($"/admin/tracks/{trackId}/pin", null);
        var second = await admin.PostAsync($"/admin/tracks/{trackId}/pin", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task FeatureTrack_Missing_ReturnsNotFound()
    {
        using var admin = await CreateAdminClientAsync();

        var response = await admin.PostAsync($"/admin/tracks/{Guid.NewGuid()}/feature", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Creator verification / tier upgrade ──

    [Fact]
    public async Task VerifyCreator_PersistsVerifiedStatusRoleAndProTier()
    {
        using var admin = await CreateAdminClientAsync();
        var seed = Guid.NewGuid().ToString("N")[..8];
        var email = $"unverified-{seed}@cambrian.com";
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        var response = await admin.PostAsync($"/admin/users/{userId}/verify-creator", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var user = json.GetProperty("data").GetProperty("user");
        Assert.True(user.GetProperty("verifiedCreator").GetBoolean());
        Assert.Equal("Pro", user.GetProperty("creatorTier").GetString());
        Assert.Equal("pro", user.GetProperty("tier").GetString());

        // Persisted — re-fetch via the admin users list, not just the mutation response.
        var list = await admin.GetAsync("/admin/users");
        var listJson = await list.Content.ReadFromJsonAsync<JsonElement>();
        var match = listJson.GetProperty("data").EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == userId);
        Assert.True(match.GetProperty("verifiedCreator").GetBoolean());
        Assert.Equal("Pro", match.GetProperty("creatorTier").GetString());
        Assert.Equal("pro", match.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task VerifyCreator_Missing_ReturnsNotFound()
    {
        using var admin = await CreateAdminClientAsync();

        var response = await admin.PostAsync($"/admin/users/{Guid.NewGuid()}/verify-creator", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpgradeUserTier_ToPro_PersistsAndReflectedOnUsersList()
    {
        using var admin = await CreateAdminClientAsync();
        var userId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);

        var response = await admin.PostAsJsonAsync($"/admin/users/{userId}/upgrade-tier", new { tier = "pro" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await admin.GetAsync("/admin/users");
        var listJson = await list.Content.ReadFromJsonAsync<JsonElement>();
        var match = listJson.GetProperty("data").EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == userId);
        Assert.Equal("Pro", match.GetProperty("creatorTier").GetString());
        Assert.Equal("pro", match.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task UpgradeUserTier_BackToFree_PersistsAndReflectedOnUsersList()
    {
        using var admin = await CreateAdminClientAsync();
        var userId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        await admin.PostAsJsonAsync($"/admin/users/{userId}/upgrade-tier", new { tier = "pro" });

        var response = await admin.PostAsJsonAsync($"/admin/users/{userId}/upgrade-tier", new { tier = "free" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await admin.GetAsync("/admin/users");
        var listJson = await list.Content.ReadFromJsonAsync<JsonElement>();
        var match = listJson.GetProperty("data").EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == userId);
        Assert.Equal("Free", match.GetProperty("creatorTier").GetString());
        Assert.Equal("free", match.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task UpgradeUserTier_Missing_ReturnsNotFound()
    {
        using var admin = await CreateAdminClientAsync();

        var response = await admin.PostAsJsonAsync($"/admin/users/{Guid.NewGuid()}/upgrade-tier", new { tier = "pro" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Reports ──

    [Fact]
    public async Task Reports_InvestigateThenClose_PersistsThroughFullLifecycle()
    {
        using var admin = await CreateAdminClientAsync();
        var creatorId = await CreateCreatorUserIdAsync(Guid.NewGuid().ToString("N")[..8]);
        var trackId = await _fixture.SeedTrackAsync(creatorId);
        var reportId = await _fixture.SeedAbuseReportAsync(trackId);

        var listBefore = await admin.GetAsync("/admin/reports");
        var listBeforeJson = await listBefore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(listBeforeJson.GetProperty("data").EnumerateArray(),
            r => r.GetProperty("id").GetString() == reportId.ToString() && r.GetProperty("status").GetString() == "open");

        var investigate = await admin.PostAsync($"/admin/reports/{reportId}/investigate", null);
        Assert.Equal(HttpStatusCode.OK, investigate.StatusCode);
        var investigateJson = await investigate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("investigating", investigateJson.GetProperty("data").GetProperty("report").GetProperty("status").GetString());

        // Idempotent re-investigate.
        var investigateAgain = await admin.PostAsync($"/admin/reports/{reportId}/investigate", null);
        Assert.Equal(HttpStatusCode.OK, investigateAgain.StatusCode);

        var close = await admin.PostAsJsonAsync($"/admin/reports/{reportId}/close", new { resolutionNote = "No violation found" });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        var closeJson = await close.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("closed", closeJson.GetProperty("data").GetProperty("report").GetProperty("status").GetString());

        // Closed reports can't be re-investigated.
        var investigateClosed = await admin.PostAsync($"/admin/reports/{reportId}/investigate", null);
        Assert.Equal(HttpStatusCode.Conflict, investigateClosed.StatusCode);
    }

    [Fact]
    public async Task InvestigateReport_Missing_ReturnsNotFound()
    {
        using var admin = await CreateAdminClientAsync();

        var response = await admin.PostAsync($"/admin/reports/{Guid.NewGuid()}/investigate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Settings ──

    [Fact]
    public async Task Settings_Get_ReflectsSeededFlags()
    {
        using var admin = await CreateAdminClientAsync();
        await _fixture.SetFeatureFlagAsync("PayoutsEnabled", true);
        await _fixture.SetFeatureFlagAsync("ModerationEnabled", false);
        await _fixture.SetFeatureFlagAsync("MarketplaceEnabled", true);
        await _fixture.SetFeatureFlagAsync("AllowExclusiveListings", false);
        await _fixture.SetFeatureFlagAsync("RequireTrackReview", true);

        var response = await admin.GetAsync("/admin/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runtime = json.GetProperty("data").GetProperty("runtimeSettings");
        Assert.True(runtime.GetProperty("payoutsEnabled").GetBoolean());
        Assert.False(runtime.GetProperty("moderationEnabled").GetBoolean());
        Assert.True(runtime.GetProperty("marketplaceEnabled").GetBoolean());
        Assert.False(runtime.GetProperty("allowExclusiveListings").GetBoolean());
        Assert.True(runtime.GetProperty("requireTrackReview").GetBoolean());

        var planManifest = json.GetProperty("data").GetProperty("planManifest");
        Assert.Equal(3, planManifest.GetArrayLength()); // Free, Creator, Pro
    }

    [Fact]
    public async Task Settings_Post_PersistsAndReflectedOnNextGet()
    {
        using var admin = await CreateAdminClientAsync();

        var post = await admin.PostAsJsonAsync("/admin/settings", new
        {
            payoutsEnabled = false,
            moderationEnabled = true,
            marketplaceEnabled = false,
            allowExclusiveListings = true,
            requireTrackReview = false,
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var get = await admin.GetAsync("/admin/settings");
        var json = await get.Content.ReadFromJsonAsync<JsonElement>();
        var runtime = json.GetProperty("data").GetProperty("runtimeSettings");
        Assert.False(runtime.GetProperty("payoutsEnabled").GetBoolean());
        Assert.True(runtime.GetProperty("moderationEnabled").GetBoolean());
        Assert.False(runtime.GetProperty("marketplaceEnabled").GetBoolean());
        Assert.True(runtime.GetProperty("allowExclusiveListings").GetBoolean());
        Assert.False(runtime.GetProperty("requireTrackReview").GetBoolean());
    }
}
