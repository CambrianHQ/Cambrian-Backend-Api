using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.E2e;
using Cambrian.Api.Tests.Fixtures;
using Xunit;

namespace Cambrian.Api.Tests.Integration.Api;

/// <summary>
/// End-to-end tests for the <c>/__e2e/*</c> support surface: secret authentication, determinism
/// of reset/seed, correctness of the seeded dataset, idempotency of simulated payments, and that
/// simulated Stripe events drive real state transitions. All tests run under the SQLite fixture
/// (no Docker) with the REAL webhook service so signature verification + dedup are exercised.
/// </summary>
[Trait("Category", "Integration")]
public sealed class E2eSupportEndpointsTests : IClassFixture<E2eApiFixture>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly E2eApiFixture _fixture;

    public E2eSupportEndpointsTests(E2eApiFixture fixture) => _fixture = fixture;

    private HttpClient SecuredClient()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(E2eSupport.SecretHeader, E2eApiFixture.E2eSecret);
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── Authentication ──

    [Fact]
    public async Task Reset_WithoutSecretHeader_Is401()
    {
        using var client = _fixture.CreateClient();
        var res = await client.PostAsync("/__e2e/reset", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Reset_WithWrongSecret_Is401()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(E2eSupport.SecretHeader, "not-the-secret");
        var res = await client.PostAsync("/__e2e/reset", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Seed_WithCorrectSecret_Is200()
    {
        using var client = SecuredClient();
        var res = await client.PostAsync("/__e2e/seed", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ── Determinism ──

    [Fact]
    public async Task SeedAndGlobalState_AreDeterministic_AcrossRuns()
    {
        using var client = SecuredClient();

        var firstSeed = await (await client.PostAsync("/__e2e/seed", null)).Content.ReadAsStringAsync();
        var firstState = await (await client.GetAsync("/__e2e/state")).Content.ReadAsStringAsync();

        var secondSeed = await (await client.PostAsync("/__e2e/seed", null)).Content.ReadAsStringAsync();
        var secondState = await (await client.GetAsync("/__e2e/state")).Content.ReadAsStringAsync();

        Assert.Equal(firstSeed, secondSeed);
        Assert.Equal(firstState, secondState);
    }

    // ── Seed correctness ──

    [Fact]
    public async Task SeededAccounts_CanLogIn()
    {
        using var client = SecuredClient();
        await client.PostAsync("/__e2e/seed", null);

        foreach (var email in new[]
        {
            E2eScenarioService.ListenerEmail,
            E2eScenarioService.CreatorEmail,
            E2eScenarioService.ProEmail,
            E2eScenarioService.EmptyCreatorEmail,
        })
        {
            // Use a fresh browser session per account. A successful login sets
            // auth_token; reusing that cookie for a later state-changing login
            // correctly requires antiforgery protection.
            using var loginClient = _fixture.CreateClient();
            var login = await loginClient.PostAsJsonAsync("/auth/login",
                new { email, password = E2eScenarioService.SeedPassword });
            Assert.True(login.IsSuccessStatusCode, $"login failed for {email}: {login.StatusCode}");
        }
    }

    [Fact]
    public async Task PlayableTrackStreams200_MissingAudioReturnsSafe503()
    {
        using var client = SecuredClient();
        var manifest = await ReadJsonAsync(await client.PostAsync("/__e2e/seed", null));

        var playableId = TrackId(manifest, "playable");
        var missingId = TrackId(manifest, "missing-audio");

        using var anon = _fixture.CreateClient();
        var playable = await anon.GetAsync($"/stream/{playableId}/audio");
        var missing = await anon.GetAsync($"/stream/{missingId}/audio");

        Assert.Equal(HttpStatusCode.OK, playable.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("media_object_missing", missingBody.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Manifest_DescribesTrackAndAudioInvariants()
    {
        using var client = SecuredClient();
        var manifest = await ReadJsonAsync(await client.PostAsync("/__e2e/seed", null));

        Assert.True(Track(manifest, "playable").GetProperty("audioAvailable").GetBoolean());
        Assert.True(Track(manifest, "playable").GetProperty("hasAuthorship").GetBoolean());

        Assert.False(Track(manifest, "missing-audio").GetProperty("audioAvailable").GetBoolean());
        Assert.False(Track(manifest, "no-authorship").GetProperty("hasAuthorship").GetBoolean());
        Assert.Equal("hidden", Track(manifest, "draft").GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task AuthorshipState_ReflectsPerTrackAuthorship()
    {
        using var client = SecuredClient();
        var manifest = await ReadJsonAsync(await client.PostAsync("/__e2e/seed", null));
        var playableId = TrackId(manifest, "playable");
        var noAuthId = TrackId(manifest, "no-authorship");

        var withAuth = await ReadJsonAsync(await client.GetAsync($"/__e2e/state/authorship?trackId={playableId}"));
        var withoutAuth = await ReadJsonAsync(await client.GetAsync($"/__e2e/state/authorship?trackId={noAuthId}"));

        Assert.True(withAuth.GetProperty("hasAuthorship").GetBoolean());
        Assert.False(withoutAuth.GetProperty("hasAuthorship").GetBoolean());
    }

    [Fact]
    public async Task ZeroTrackCreator_HasNoTracks()
    {
        using var client = SecuredClient();
        await client.PostAsync("/__e2e/seed", null);

        var state = await ReadJsonAsync(
            await client.GetAsync($"/__e2e/state/authorship?email={E2eScenarioService.EmptyCreatorEmail}"));

        Assert.Empty(state.GetProperty("tracks").EnumerateArray());
    }

    // ── Payment simulation idempotency ──

    [Fact]
    public async Task CreditCheckout_DuplicateEventId_GrantsOnce()
    {
        using var client = SecuredClient();
        await client.PostAsync("/__e2e/seed", null);

        var before = await PurchasedCreditsAsync(client, E2eScenarioService.ProEmail);

        var body = new { email = E2eScenarioService.ProEmail, kind = "credits", credits = 3, eventId = "evt_e2e_dupe_credits" };
        var first = await client.PostAsJsonAsync("/__e2e/stripe/checkout-completed", body);
        var second = await client.PostAsJsonAsync("/__e2e/stripe/checkout-completed", body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // The second delivery of the same event id must be recognised as a duplicate.
        var secondResult = await ReadJsonAsync(second);
        Assert.True(secondResult.GetProperty("deduplicated").GetBoolean());

        var after = await PurchasedCreditsAsync(client, E2eScenarioService.ProEmail);
        Assert.Equal(before + 3, after);
    }

    // ── Simulated events drive real state ──

    [Fact]
    public async Task SubscriptionLifecycle_ActivatesThenCancels()
    {
        using var client = SecuredClient();
        await client.PostAsync("/__e2e/seed", null);

        var email = E2eScenarioService.ListenerEmail; // starts free, no subscription

        await client.PostAsJsonAsync("/__e2e/stripe/checkout-completed",
            new { email, kind = "subscription", tier = "creator", eventId = "evt_e2e_sub_activate" });

        var afterCheckout = await ReadJsonAsync(await client.GetAsync($"/__e2e/state/payment?email={email}"));
        Assert.Contains(afterCheckout.GetProperty("subscriptions").EnumerateArray(),
            s => s.GetProperty("plan").GetString() == "creator" && s.GetProperty("status").GetString() == "active");

        await client.PostAsJsonAsync("/__e2e/stripe/subscription-cancelled",
            new { email, eventId = "evt_e2e_sub_cancel" });

        var afterCancel = await ReadJsonAsync(await client.GetAsync($"/__e2e/state/payment?email={email}"));
        Assert.Contains(afterCancel.GetProperty("subscriptions").EnumerateArray(),
            s => s.GetProperty("status").GetString() == "cancelled");
    }

    [Fact]
    public async Task CheckoutCancelled_IsNoOp()
    {
        using var client = SecuredClient();
        await client.PostAsync("/__e2e/seed", null);

        var before = await PurchasedCreditsAsync(client, E2eScenarioService.ProEmail);

        var res = await client.PostAsJsonAsync("/__e2e/stripe/checkout-cancelled",
            new { email = E2eScenarioService.ProEmail });
        var body = await ReadJsonAsync(res);

        Assert.False(body.GetProperty("processed").GetBoolean());
        Assert.Equal(before, await PurchasedCreditsAsync(client, E2eScenarioService.ProEmail));
    }

    // ── Helpers ──

    private static JsonElement Track(JsonElement manifest, string kind)
        => manifest.GetProperty("tracks").EnumerateArray()
            .First(t => t.GetProperty("kind").GetString() == kind);

    private static string TrackId(JsonElement manifest, string kind)
        => Track(manifest, kind).GetProperty("trackId").GetString()!;

    private async Task<int> PurchasedCreditsAsync(HttpClient client, string email)
    {
        var state = await ReadJsonAsync(await client.GetAsync($"/__e2e/state/credit?email={email}"));
        return state.GetProperty("purchased").GetInt32();
    }
}
