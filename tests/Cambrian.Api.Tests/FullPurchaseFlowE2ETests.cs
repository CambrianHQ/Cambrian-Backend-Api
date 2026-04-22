using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// Single end-to-end flow exercised entirely through HTTP: a creator uploads a
/// track, edits its metadata, a buyer triggers the Stripe webhook, and the
/// buyer then sees the track in their library and can stream the audio.
/// This is the one test that chains every surface a customer touches so a
/// regression in any link of the chain fails here.
/// </summary>
[Trait("Category", "Critical")]
public sealed class FullPurchaseFlowE2ETests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public FullPurchaseFlowE2ETests(CambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upload_Edit_Purchase_Webhook_Library_Playback()
    {
        // ── 1. Creator onboarding ────────────────────────────────
        var creatorEmail = $"e2e-creator-{Guid.NewGuid():N}@cambrian.com";
        var creatorUsername = $"e2ec{Guid.NewGuid():N}"[..12];
        using var creatorClient = await _fixture.CreateRoleClientAsync(
            email: creatorEmail,
            password: "Test1234!@",
            role: "Creator",
            username: creatorUsername);

        // ── 2. HTTP upload ───────────────────────────────────────
        using var uploadBody = new MultipartFormDataContent
        {
            { new StringContent("E2E Flow Beat"), "Title" },
            { new StringContent("Original description"), "Description" },
            { new StringContent("999"), "NonExclusivePriceCents" },
        };
        var audio = new ByteArrayContent(new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        uploadBody.Add(audio, "Audio", "e2e-track.mp3");

        var uploadRes = await creatorClient.PostAsync("/upload", uploadBody);
        Assert.Equal(HttpStatusCode.Created, uploadRes.StatusCode);

        var uploadJson = await uploadRes.Content.ReadFromJsonAsync<JsonElement>();
        var uploadData = uploadJson.GetProperty("data");
        var trackIdString = uploadData.GetProperty("trackId").GetString()!;
        var trackId = Guid.Parse(trackIdString);

        // ── 3. HTTP edit ─────────────────────────────────────────
        var editRes = await creatorClient.PutAsJsonAsync($"/creator/tracks/{trackId}", new
        {
            title = "E2E Flow Beat (edited)",
            description = "Edited description",
            nonExclusivePriceCents = 1499,
        });
        Assert.Equal(HttpStatusCode.OK, editRes.StatusCode);

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var track = await db.Tracks.FirstAsync(t => t.Id == trackId);
            Assert.Equal("E2E Flow Beat (edited)", track.Title);
            Assert.Equal(1499, track.NonExclusivePriceCents);
            // The track must be streamable by anyone — it was uploaded public.
            Assert.Equal("public", track.Visibility);
        }

        // ── 4. Buyer onboarding ──────────────────────────────────
        var buyerEmail = $"e2e-buyer-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _fixture.GetUserIdAsync(buyerEmail);
        var buyerToken = await _fixture.LoginUserAsync(buyerEmail, "Test1234!@");
        using var buyerClient = _fixture.CreateClient();
        buyerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", buyerToken);

        // ── 5. Stripe webhook drives the purchase ────────────────
        using var anonymousClient = _fixture.CreateClient();
        var webhookPayload = $$"""
        {
            "id": "evt_e2e_{{Guid.NewGuid():N}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 1499
                }
            }
        }
        """;
        var webhookRes = await anonymousClient.PostAsync(
            "/webhook/stripe",
            new StringContent(webhookPayload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, webhookRes.StatusCode);

        // ── 6. Buyer sees the track in their library ─────────────
        var libraryRes = await buyerClient.GetAsync("/library");
        Assert.Equal(HttpStatusCode.OK, libraryRes.StatusCode);
        var libraryJson = await libraryRes.Content.ReadFromJsonAsync<JsonElement>();
        var libraryData = libraryJson.GetProperty("data");
        Assert.True(libraryData.GetArrayLength() > 0, "buyer library should contain at least one item");

        var libraryHasTrack = false;
        foreach (var item in libraryData.EnumerateArray())
        {
            if (item.TryGetProperty("trackId", out var tid) &&
                Guid.TryParse(tid.GetString(), out var g) &&
                g == trackId)
            {
                libraryHasTrack = true;
                break;
            }
        }
        Assert.True(libraryHasTrack, $"buyer library did not contain track {trackId}");

        // ── 7. Playback ──────────────────────────────────────────
        var audioRes = await buyerClient.GetAsync($"/stream/{trackId}/audio");
        Assert.True(
            audioRes.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent,
            $"expected 200 or 206 from /stream, got {(int)audioRes.StatusCode}");
        Assert.Equal("audio/mpeg", audioRes.Content.Headers.ContentType?.MediaType);

        // ── 8. Idempotency cross-check ───────────────────────────
        using var scope2 = _fixture.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var purchases = await db2.Purchases
            .Where(p => p.BuyerId == buyerId && p.TrackId == trackId)
            .ToListAsync();
        Assert.Single(purchases);
        Assert.Equal("completed", purchases[0].Status);
        Assert.Equal(1499, purchases[0].AmountCents);
    }
}
