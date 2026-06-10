using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

public class AuthorshipRecordTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public AuthorshipRecordTests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_ReturnsCheckoutUrl_AndPendingRecord()
    {
        var (client, _, trackId) = await SetupCreatorWithTrackAsync();

        var res = await client.PostAsJsonAsync($"/api/releases/{trackId}/authorship-record", EvidenceBody());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var recordId = data.GetProperty("recordId").GetGuid();
        Assert.StartsWith("https://", data.GetProperty("checkoutUrl").GetString());

        var get = await client.GetAsync($"/api/authorship-records/{recordId}");
        var record = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("pending_payment", record.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("certificate").ValueKind);
    }

    [Fact]
    public async Task PaidWebhook_IssuesRecord_SignatureVerifies_WithPublishedKey()
    {
        var (client, userId, trackId) = await SetupCreatorWithTrackAsync();
        var recordId = await CreatePendingRecordAsync(client, trackId);

        await FirePaidWebhookAsync(userId, recordId, $"cs_test_{Guid.NewGuid():N}");

        // Owner view now carries the certificate.
        var get = await client.GetAsync($"/api/authorship-records/{recordId}");
        var record = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("issued", record.GetProperty("status").GetString());

        var cert = record.GetProperty("certificate");
        var canonical = cert.GetProperty("canonicalRecord").GetString()!;
        var recordHash = cert.GetProperty("recordHash").GetString()!;
        var signature = cert.GetProperty("signature").GetString()!;
        var issuedAt = cert.GetProperty("issuedAt").GetDateTime();

        // 1. The hash matches the canonical record.
        var recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        Assert.Equal(recordHash, recomputed);

        // 2. The signature verifies against the platform public key.
        using var scope = _fixture.Services.CreateScope();
        var signer = scope.ServiceProvider.GetRequiredService<IProvenanceSigner>();
        Assert.True(signer.Verify(recordHash, issuedAt.ToUniversalTime(), signature));

        // 3. Evidence manifest is embedded with per-file SHA-256 entries.
        using var doc = JsonDocument.Parse(canonical);
        var manifest = doc.RootElement.GetProperty("evidenceManifest").EnumerateArray().ToList();
        Assert.Equal(2, manifest.Count);
        Assert.All(manifest, e => Assert.Equal(64, e.GetProperty("sha256").GetString()!.Length));
    }

    [Fact]
    public async Task Verify_IsPublic_AndExposesNoPiiBeyondArtistName()
    {
        var (client, userId, trackId) = await SetupCreatorWithTrackAsync();
        var recordId = await CreatePendingRecordAsync(client, trackId);
        await FirePaidWebhookAsync(userId, recordId, $"cs_test_{Guid.NewGuid():N}");

        // Anonymous client — no Authorization header.
        var anonymous = _fixture.CreateClient();
        var res = await anonymous.GetAsync($"/verify/{recordId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var raw = await res.Content.ReadAsStringAsync();
        var cert = JsonDocument.Parse(raw).RootElement.GetProperty("data");

        Assert.False(string.IsNullOrEmpty(cert.GetProperty("artistName").GetString()));
        Assert.False(string.IsNullOrEmpty(cert.GetProperty("publicKeyPem").GetString()));
        Assert.False(string.IsNullOrEmpty(cert.GetProperty("verificationInstructions").GetString()));

        // No PII beyond the artist name: the owner's user id and email never appear.
        Assert.DoesNotContain(userId, raw);
        Assert.DoesNotContain("@cambrian.com", raw);
    }

    [Fact]
    public async Task Verify_PendingRecord_Returns404()
    {
        var (client, _, trackId) = await SetupCreatorWithTrackAsync();
        var recordId = await CreatePendingRecordAsync(client, trackId);

        var anonymous = _fixture.CreateClient();
        var res = await anonymous.GetAsync($"/verify/{recordId}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task PaidWebhook_Replay_IsIdempotent()
    {
        var (client, userId, trackId) = await SetupCreatorWithTrackAsync();
        var recordId = await CreatePendingRecordAsync(client, trackId);

        var sessionId = $"cs_test_{Guid.NewGuid():N}";
        await FirePaidWebhookAsync(userId, recordId, sessionId, eventId: "evt_authorship_replay_1");
        var firstIssuedAt = await GetIssuedAtAsync(recordId);

        // Same event id (Stripe retry) and a different delivery of the same session.
        await FirePaidWebhookAsync(userId, recordId, sessionId, eventId: "evt_authorship_replay_1");
        await FirePaidWebhookAsync(userId, recordId, sessionId, eventId: "evt_authorship_replay_2");

        Assert.Equal(firstIssuedAt, await GetIssuedAtAsync(recordId));
    }

    [Fact]
    public async Task Create_ForSomeoneElsesTrack_Returns404()
    {
        var (_, ownerId, trackId) = await SetupCreatorWithTrackAsync();
        _ = ownerId;
        var stranger = await _fixture.CreateAuthenticatedClientAsync($"ar-stranger-{Guid.NewGuid():N}@cambrian.com");

        var res = await stranger.PostAsJsonAsync($"/api/releases/{trackId}/authorship-record", EvidenceBody());
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── Helpers ──

    private static object EvidenceBody() => new
    {
        evidence = new[]
        {
            new { fileKey = "evidence/project-file.zip", description = "DAW project" },
            new { fileKey = "evidence/voice-memo.m4a", description = "Original melody memo" },
        },
        declarations = new[] { "I wrote all lyrics.", "Melody composed before any AI involvement." },
        narrative = "Started as a voice memo; arranged over two weeks.",
        generator = new { tool = "Suno", version = "v4", prompts = new[] { "dark trap beat 140bpm" } },
    };

    private async Task<(HttpClient Client, string UserId, Guid TrackId)> SetupCreatorWithTrackAsync()
    {
        var email = $"authorship-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId, "Authorship Test Track");
        return (client, userId, trackId);
    }

    private static async Task<Guid> CreatePendingRecordAsync(HttpClient client, Guid trackId)
    {
        var res = await client.PostAsJsonAsync($"/api/releases/{trackId}/authorship-record", EvidenceBody());
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("recordId").GetGuid();
    }

    /// <summary>Drive the real webhook path (TestWebhookService → StripeWebhookService.ProcessEventAsync).</summary>
    private async Task FirePaidWebhookAsync(string userId, Guid recordId, string sessionId, string? eventId = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var webhook = scope.ServiceProvider.GetRequiredService<IWebhookService>();
        var payload = JsonSerializer.Serialize(new
        {
            id = eventId ?? $"evt_{Guid.NewGuid():N}",
            type = "checkout.session.completed",
            data = new
            {
                @object = new Dictionary<string, object?>
                {
                    ["id"] = sessionId,
                    ["client_reference_id"] = $"{userId}:authorship:{recordId}",
                    ["amount_total"] = 2900,
                },
            },
        });
        await webhook.HandleStripeAsync(payload, "test-signature");
    }

    private async Task<DateTime?> GetIssuedAtAsync(Guid recordId)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var record = await db.AuthorshipRecords.AsNoTracking().FirstAsync(r => r.Id == recordId);
        Assert.Equal("issued", record.Status);
        return record.IssuedAt;
    }
}
