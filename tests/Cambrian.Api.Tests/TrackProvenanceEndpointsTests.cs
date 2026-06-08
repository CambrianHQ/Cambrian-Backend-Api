using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Startup;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.DTOs.Catalog;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Provenance;
using Cambrian.Application.Services;
using Cambrian.Domain.Enums;
using Cambrian.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cambrian.Api.Tests;

/// <summary>
/// Integration tests for the §9 provenance/authorship/compliance endpoints: plan gating (402),
/// reads (Free+), the authorship workflow (Creator+, owner), the creator-detail bundle, plus
/// content-hash + signed stamp on upload, the backfill, and the public verify endpoints.
/// </summary>
public sealed class TrackProvenanceEndpointsTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public TrackProvenanceEndpointsTests(CambrianApiFixture fixture) => _fixture = fixture;

    private static string NewEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.com";

    private async Task<(HttpClient client, string userId)> CreatorClientAsync(string prefix)
    {
        var email = NewEmail(prefix);
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");
        await _fixture.SetCreatorTierAsync(email, CreatorTier.Creator);
        var userId = await _fixture.GetUserIdAsync(email);
        return (client, userId);
    }

    // ── Gating: under-entitled (Free) → 402 UPGRADE_REQUIRED ──

    [Fact]
    public async Task PostAuthorship_AsFreeUser_Returns402UpgradeRequired()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(NewEmail("free"), "Test1234!@");

        var res = await client.PostAsJsonAsync(
            $"/api/tracks/{Guid.NewGuid()}/authorship", new { edits = "x", lyricsAuthored = true });

        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UPGRADE_REQUIRED", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetCreatorDetail_AsFreeUser_Returns402UpgradeRequired()
    {
        var client = await _fixture.CreateAuthenticatedClientAsync(NewEmail("free"), "Test1234!@");
        var res = await client.GetAsync($"/api/tracks/{Guid.NewGuid()}/creator-detail");
        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
    }

    // ── Reads are Free+ ──

    [Fact]
    public async Task GetProvenance_AsFreeUser_ReturnsPendingAnchorForUnstampedTrack()
    {
        var email = NewEmail("free-read");
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId);

        var res = await client.GetAsync($"/api/tracks/{trackId}/provenance");

        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("contentHash").ValueKind);
        Assert.Equal("pending", data.GetProperty("anchor").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetComplianceScore_AsFreeUser_ReturnsScoreAndFiveChecks()
    {
        var email = NewEmail("free-score");
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId);

        var res = await client.GetAsync($"/api/tracks/{trackId}/compliance-score");

        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetProperty("score").GetInt32() is >= 0 and <= 100);
        Assert.Equal(5, data.GetProperty("checks").GetArrayLength());
    }

    // ── Authorship workflow (Creator+, owner) ──

    [Fact]
    public async Task Authorship_UpsertThenGet_RoundTripsAndPersistsRights()
    {
        var (client, userId) = await CreatorClientAsync("creator");
        var trackId = await _fixture.SeedTrackAsync(userId);

        var post = await client.PostAsJsonAsync($"/api/tracks/{trackId}/authorship", new
        {
            edits = "Mixed and mastered",
            lyricsAuthored = true,
            aiDisclosure = "No generative AI used",
            commercialRightsVerified = true,
        });
        post.EnsureSuccessStatusCode();

        var get = await client.GetAsync($"/api/tracks/{trackId}/authorship");
        get.EnsureSuccessStatusCode();
        var data = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Equal("Mixed and mastered", data.GetProperty("edits").GetString());
        Assert.True(data.GetProperty("lyricsAuthored").GetBoolean());
        Assert.True(data.GetProperty("commercialRightsVerified").GetBoolean());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.AsNoTracking().FirstAsync(t => t.Id == trackId);
        Assert.True(track.CommercialRightsVerified);
    }

    [Fact]
    public async Task Authorship_SecondPost_UpsertsInPlace()
    {
        var (client, userId) = await CreatorClientAsync("creator-upsert");
        var trackId = await _fixture.SeedTrackAsync(userId);

        await client.PostAsJsonAsync($"/api/tracks/{trackId}/authorship", new { edits = "first", commercialRightsVerified = true });
        await client.PostAsJsonAsync($"/api/tracks/{trackId}/authorship", new { edits = "second", commercialRightsVerified = false });

        var get = await client.GetAsync($"/api/tracks/{trackId}/authorship");
        var data = (await get.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("second", data.GetProperty("edits").GetString());
        Assert.False(data.GetProperty("commercialRightsVerified").GetBoolean());

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        Assert.Equal(1, await db.TrackAuthorships.CountAsync(a => a.TrackId == trackId));
    }

    [Fact]
    public async Task PostAuthorship_OnAnotherCreatorsTrack_Returns403()
    {
        var (client, _) = await CreatorClientAsync("creator-nonowner");

        // Owner must be a real user — GetByIdAsync inner-joins Creator, so a track owned by a
        // non-existent user would 404 before the ownership check.
        var otherEmail = NewEmail("other-owner");
        await _fixture.RegisterUserAsync(otherEmail, "Test1234!@");
        var otherUserId = await _fixture.GetUserIdAsync(otherEmail);
        var foreignTrackId = await _fixture.SeedTrackAsync(otherUserId);

        var res = await client.PostAsJsonAsync($"/api/tracks/{foreignTrackId}/authorship",
            new { edits = "should be blocked", commercialRightsVerified = true });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── Creator-detail bundle ──

    [Fact]
    public async Task GetCreatorDetail_AsOwner_BundlesProvenanceComplianceAuthorship()
    {
        var (client, userId) = await CreatorClientAsync("creator-bundle");
        var trackId = await _fixture.SeedTrackAsync(userId);
        await client.PostAsJsonAsync($"/api/tracks/{trackId}/authorship",
            new { edits = "documented", aiDisclosure = "No AI", commercialRightsVerified = true });

        var res = await client.GetAsync($"/api/tracks/{trackId}/creator-detail");

        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("pending", data.GetProperty("provenance").GetProperty("anchor").GetProperty("status").GetString());
        Assert.Equal("documented", data.GetProperty("authorship").GetProperty("edits").GetString());
        Assert.True(data.GetProperty("verification").GetProperty("commercialRightsVerified").GetBoolean());
    }

    // ── Content hash + signed stamp + pending anchor on upload ──

    [Fact]
    public async Task Upload_HashesAndSigns_AndRecordsPendingAnchor()
    {
        var email = NewEmail("uploader");
        await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        var bytes = new byte[2048];
        bytes[0] = 0xFF; bytes[1] = 0xFB; // mp3 magic
        var expectedHash = ContentHashing.ComputeSha256Hex(new MemoryStream(bytes));

        Guid trackId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var upload = scope.ServiceProvider.GetRequiredService<IUploadService>();
            var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Audio", "beat.mp3")
            {
                Headers = new HeaderDictionary(),
                ContentType = "audio/mpeg",
            };
            var result = await upload.Upload(new UploadTrackRequest { Audio = file, Title = "Hashed Beat", CreatorId = userId });
            trackId = Guid.Parse(result.TrackId);
        }

        using var verify = _fixture.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await db.Tracks.AsNoTracking().FirstAsync(t => t.Id == trackId);
        Assert.Equal(expectedHash, track.ContentHash);
        Assert.False(string.IsNullOrEmpty(track.Signature));   // signed stamp issued
        Assert.NotNull(track.SignedAt);

        var anchor = await db.ProvenanceAnchors.AsNoTracking().FirstAsync(a => a.TrackId == trackId);
        Assert.Equal("pending", anchor.Status);                 // no chain write this batch
        Assert.Null(anchor.RootTxRef);
        Assert.Equal(expectedHash, anchor.ContentHash);
    }

    // ── Public verify surface ──

    [Fact]
    public async Task PublicKeyEndpoint_IsAnonymous_AndReturnsKey()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/api/provenance/public-key");

        res.EnsureSuccessStatusCode();
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("ECDSA-P256-SHA256", data.GetProperty("algorithm").GetString());
        Assert.Contains("BEGIN PUBLIC KEY", data.GetProperty("publicKeyPem").GetString());
    }

    [Fact]
    public async Task VerifyEndpoint_ValidatesGenuineStampAndRejectsTampered()
    {
        // Sign via the same singleton signer the endpoint uses.
        ProvenanceStamp stamp;
        var hash = ContentHashing.ComputeSha256Hex(new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        using (var scope = _fixture.Services.CreateScope())
        {
            var signer = scope.ServiceProvider.GetRequiredService<IProvenanceSigner>();
            stamp = signer.Sign(hash, DateTime.UtcNow);
        }

        var client = _fixture.CreateClient();

        var good = await client.PostAsJsonAsync("/api/provenance/verify",
            new { contentHash = hash, signedAt = stamp.SignedAt, signature = stamp.Signature });
        var goodData = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(goodData.GetProperty("valid").GetBoolean());

        var bad = await client.PostAsJsonAsync("/api/provenance/verify",
            new { contentHash = hash, signedAt = stamp.SignedAt.AddSeconds(5), signature = stamp.Signature });
        var badData = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(badData.GetProperty("valid").GetBoolean());
    }

    // ── Backfill (hash + signed stamp) ──

    [Fact]
    public async Task Backfill_HashesAndSignsTracksWithNullContentHash()
    {
        var email = NewEmail("backfill");
        await _fixture.RegisterUserAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);
        var trackId = await _fixture.SeedTrackAsync(userId, "Legacy Track");

        TrackContentHashBackfillResult result;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
            var signer = scope.ServiceProvider.GetRequiredService<IProvenanceSigner>();
            result = await TrackContentHashBackfill.RunAsync(db, storage, signer);
        }

        Assert.True(result.Hashed >= 1);

        using var verify = _fixture.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var track = await verifyDb.Tracks.AsNoTracking().FirstAsync(t => t.Id == trackId);
        Assert.Equal(64, track.ContentHash!.Length);
        Assert.False(string.IsNullOrEmpty(track.Signature));
    }

    // ── Batched Merkle anchoring (job) + inclusion verification ──

    [Fact]
    public async Task AnchorBatch_FlipsTrackToAnchored_AndInclusionVerifies()
    {
        var email = NewEmail("anchor");
        var client = await _fixture.CreateAuthenticatedClientAsync(email, "Test1234!@");
        var userId = await _fixture.GetUserIdAsync(email);

        // Upload a real track → pending anchor + hash + stamp.
        var bytes = new byte[1500];
        bytes[0] = 0xFF; bytes[1] = 0xFB;
        Guid trackId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var upload = scope.ServiceProvider.GetRequiredService<IUploadService>();
            var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "Audio", "beat.mp3")
            {
                Headers = new HeaderDictionary(),
                ContentType = "audio/mpeg",
            };
            var r = await upload.Upload(new UploadTrackRequest { Audio = file, Title = "Anchor Me", CreatorId = userId });
            trackId = Guid.Parse(r.TrackId);
        }

        // Run the batch job (NoOp anchor → real Merkle root + proofs).
        int anchored;
        using (var scope = _fixture.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<ProvenanceAnchorBatchProcessor>();
            anchored = await processor.ProcessBatchAsync(1000);
        }
        Assert.True(anchored >= 1);

        // Provenance read now reports anchored with chain/root/tx/proof.
        var prov = (await (await client.GetAsync($"/api/tracks/{trackId}/provenance"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var anchor = prov.GetProperty("anchor");
        Assert.Equal("anchored", anchor.GetProperty("status").GetString());
        Assert.Equal("dev", anchor.GetProperty("chain").GetString());
        Assert.False(string.IsNullOrEmpty(anchor.GetProperty("rootTxRef").GetString()));

        var hash = prov.GetProperty("contentHash").GetString()!;
        var proof = anchor.GetProperty("merkleProof").GetString()!;
        var root = anchor.GetProperty("merkleRoot").GetString()!;

        // Inclusion verifies on the anonymous endpoint; a different hash does not.
        var anon = _fixture.CreateClient();
        var good = (await (await anon.PostAsJsonAsync("/api/provenance/verify-inclusion",
            new { contentHash = hash, merkleProof = proof, merkleRoot = root }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(good.GetProperty("valid").GetBoolean());

        var otherHash = ContentHashing.ComputeSha256Hex(new MemoryStream(new byte[] { 7, 7, 7, 7 }));
        var bad = (await (await anon.PostAsJsonAsync("/api/provenance/verify-inclusion",
            new { contentHash = otherHash, merkleProof = proof, merkleRoot = root }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(bad.GetProperty("valid").GetBoolean());

        // Compliance's provenance check flips to pass once anchored.
        var comp = (await (await client.GetAsync($"/api/tracks/{trackId}/compliance-score"))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var provCheck = comp.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "provenanceAnchored");
        Assert.Equal("pass", provCheck.GetProperty("status").GetString());
    }
}
