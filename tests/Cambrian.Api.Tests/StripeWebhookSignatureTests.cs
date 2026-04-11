using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

/// <summary>
/// End-to-end webhook tests that keep the real Stripe signature verification in
/// place so the HTTP pipeline mirrors production more closely.
/// </summary>
public sealed class StripeWebhookSignatureTests : IClassFixture<SignedStripeWebhookApiFixture>
{
    private readonly SignedStripeWebhookApiFixture _fixture;

    public StripeWebhookSignatureTests(SignedStripeWebhookApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Webhook_With_Valid_Signature_Is_Processed()
    {
        var creatorEmail = $"signed-wh-creator-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(creatorEmail, "Test1234!@");
        var creatorId = await _fixture.GetUserIdAsync(creatorEmail);
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Signed Webhook Beat");

        var buyerEmail = $"signed-wh-buyer-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(buyerEmail, "Test1234!@");
        var buyerId = await _fixture.GetUserIdAsync(buyerEmail);

        var payload = $$"""
        {
            "object": "event",
            "id": "evt_signed_{{Guid.NewGuid():N}}",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "object": "checkout.session",
                    "id": "cs_signed_{{Guid.NewGuid():N}}",
                    "client_reference_id": "{{buyerId}}:{{trackId}}:non-exclusive",
                    "amount_total": 2999
                }
            }
        }
        """;

        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", CreateStripeSignature(payload));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        var purchase = await db.Purchases
            .FirstOrDefaultAsync(p => p.BuyerId == buyerId && p.TrackId == trackId);
        Assert.NotNull(purchase);

        var libraryItem = await db.Library
            .FirstOrDefaultAsync(l => l.UserId == buyerId && l.TrackId == trackId);
        Assert.NotNull(libraryItem);
    }

    [Fact]
    public async Task Webhook_With_Invalid_Signature_Returns_BadRequest()
    {
        const string payload = """
        {
            "object": "event",
            "id": "evt_invalid_sig",
            "type": "checkout.session.completed",
            "data": {
                "object": {
                    "object": "checkout.session",
                    "id": "cs_invalid_sig"
                }
            }
        }
        """;

        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", "t=1234567890,v1=deadbeef");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string CreateStripeSignature(string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = $"{timestamp}.{payload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SignedStripeWebhookApiFixture.WebhookSecret));
        var digest = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();

        return $"t={timestamp},v1={digest}";
    }
}
