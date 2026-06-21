using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Application.Interfaces;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests;

public class ArtistMonetizationTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public ArtistMonetizationTests(CambrianApiFixture fixture) => _fixture = fixture;

    // ── Tips ──

    [Fact]
    public async Task Tip_ArtistWithoutConnectedAccount_Returns409()
    {
        var artistId = await SeedArtistAsync(connected: false);
        var fan = await CreateFanAsync();

        var res = await fan.PostAsJsonAsync($"/api/artists/{artistId}/tip", new { amountCents = 500 });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Tip_CreatesConnectedCheckout_WithZeroApplicationFee()
    {
        var artistId = await SeedArtistAsync(connected: true);
        var fan = await CreateFanAsync();

        var res = await fan.PostAsJsonAsync($"/api/artists/{artistId}/tip", new { amountCents = 500 });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.StartsWith("https://", data.GetProperty("checkoutUrl").GetString());

        var call = Gateway().ConnectedCheckouts.Last();
        Assert.Equal(500, call.AmountCents);
        Assert.Equal(0, call.ApplicationFeeCents); // launch: tips carry no platform fee
        Assert.Contains($":tip:{artistId}", call.ClientReferenceId);
    }

    [Fact]
    public async Task Tip_BelowMinimum_Returns400()
    {
        var artistId = await SeedArtistAsync(connected: true);
        var fan = await CreateFanAsync();

        var res = await fan.PostAsJsonAsync($"/api/artists/{artistId}/tip", new { amountCents = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── Fan subscriptions ──

    [Fact]
    public async Task Subscribe_ArtistWithoutPrice_Returns409()
    {
        var artistId = await SeedArtistAsync(connected: true, subscriptionPriceCents: null);
        var fan = await CreateFanAsync();

        var res = await fan.PostAsync($"/api/artists/{artistId}/subscribe", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Subscribe_CreatesConnectedSubscription_AtArtistPrice_With15PercentFee()
    {
        var artistId = await SeedArtistAsync(connected: true, subscriptionPriceCents: 700);
        var fan = await CreateFanAsync();

        var res = await fan.PostAsync($"/api/artists/{artistId}/subscribe", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var call = Gateway().ConnectedSubscriptions.Last();
        Assert.Equal(700, call.AmountCents);          // always the artist-set price
        Assert.Equal(15m, call.ApplicationFeePercent); // 15% platform fee on subs
    }

    [Fact]
    public async Task SetSubscriptionPrice_PersistsOnArtist()
    {
        var email = $"price-artist-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);

        var res = await client.PutAsJsonAsync("/api/artists/me/subscription-price", new { priceCents = 900 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Email == email);
        Assert.Equal(900, user.FanSubscriptionPriceCents);
    }

    // ── Connect webhooks → earnings ledger ──

    [Fact]
    public async Task ConnectWebhook_TipCompleted_WritesEarnings_AndIsIdempotent()
    {
        var artistId = await SeedArtistAsync(connected: true);
        var sessionId = $"cs_tip_{Guid.NewGuid():N}";

        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            "evt_tip_1_" + sessionId, "checkout.session.completed", "acct_fake_123",
            clientReferenceId: $"fan-user-1:tip:{artistId}",
            sessionId: sessionId,
            amountTotal: 500));

        // Stripe retry (same event id) + duplicate delivery (new event id, same session).
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            "evt_tip_1_" + sessionId, "checkout.session.completed", "acct_fake_123",
            clientReferenceId: $"fan-user-1:tip:{artistId}",
            sessionId: sessionId,
            amountTotal: 500));
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            "evt_tip_2_" + sessionId, "checkout.session.completed", "acct_fake_123",
            clientReferenceId: $"fan-user-1:tip:{artistId}",
            sessionId: sessionId,
            amountTotal: 500));

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var rows = await db.EarningsTransactions
            .Where(t => t.ArtistUserId == artistId && t.ExternalRef == sessionId)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("tip", row.Source);
        Assert.Equal(500, row.GrossCents);
        Assert.Equal(0, row.FeeCents);
        Assert.Equal(500, row.NetCents);
        Assert.Equal("fan-user-1", row.PayerUserId);
    }

    [Fact]
    public async Task ConnectWebhook_FanSubLifecycle_Activates_RecordsEarnings_Renews_Cancels()
    {
        var artistId = await SeedArtistAsync(connected: true, subscriptionPriceCents: 1000);
        var fanEmail = $"fansub-fan-{Guid.NewGuid():N}@cambrian.com";
        var fan = await _fixture.CreateAuthenticatedClientAsync(fanEmail);
        var fanUserId = await _fixture.GetUserIdAsync(fanEmail);

        // Subscribe → pending FanSubscription row + checkout.
        var res = await fan.PostAsync($"/api/artists/{artistId}/subscribe", null);
        res.EnsureSuccessStatusCode();
        var fanSubId = Gateway().ConnectedSubscriptions.Last().ClientReferenceId.Split(':')[2];

        var sessionId = $"cs_fansub_{Guid.NewGuid():N}";
        var stripeSubId = $"sub_{Guid.NewGuid():N}";

        // 1. checkout.session.completed → active + first-period earnings (15% fee).
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            $"evt_fs1_{sessionId}", "checkout.session.completed", "acct_fake_123",
            clientReferenceId: $"{fanUserId}:fansub:{fanSubId}",
            sessionId: sessionId,
            amountTotal: 1000,
            sessionSubscriptionId: stripeSubId));

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
            var sub = await db.FanSubscriptions.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(fanSubId));
            Assert.Equal("active", sub.Status);
            Assert.Equal(stripeSubId, sub.StripeSubscriptionId);

            var first = await db.EarningsTransactions.AsNoTracking()
                .FirstAsync(t => t.ExternalRef == sessionId);
            Assert.Equal("sub", first.Source);
            Assert.Equal(1000, first.GrossCents);
            Assert.Equal(150, first.FeeCents);  // gross − floor(gross × 0.85)
            Assert.Equal(850, first.NetCents);
        }

        // 2. invoice.paid subscription_create is skipped (already recorded from the session).
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            $"evt_fs2_{sessionId}", "invoice.paid", "acct_fake_123",
            invoiceId: $"in_create_{sessionId}",
            invoiceSubscriptionId: stripeSubId,
            billingReason: "subscription_create",
            amountPaid: 1000));

        // 3. invoice.paid subscription_cycle → renewal earnings.
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            $"evt_fs3_{sessionId}", "invoice.paid", "acct_fake_123",
            invoiceId: $"in_cycle_{sessionId}",
            invoiceSubscriptionId: stripeSubId,
            billingReason: "subscription_cycle",
            amountPaid: 1000));

        // 4. customer.subscription.deleted → cancelled.
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            $"evt_fs4_{sessionId}", "customer.subscription.deleted", "acct_fake_123",
            subscriptionId: stripeSubId));

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

            var rows = await db.EarningsTransactions.AsNoTracking()
                .Where(t => t.ArtistUserId == artistId && t.Source == "sub")
                .Where(t => t.ExternalRef == sessionId || t.ExternalRef.Contains(sessionId))
                .ToListAsync();
            Assert.Equal(2, rows.Count); // first period + one renewal; create-invoice skipped
            Assert.All(rows, r => Assert.Equal(850, r.NetCents));

            var sub = await db.FanSubscriptions.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(fanSubId));
            Assert.Equal("cancelled", sub.Status);
            Assert.NotNull(sub.CancelledAt);
        }
    }

    [Fact]
    public async Task ConnectWebhook_MissingEventId_IsRejectedBeforeProcessing()
    {
        var artistId = await SeedArtistAsync(connected: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
                null,
                "checkout.session.completed",
                "acct_fake_123",
                clientReferenceId: $"fan-user:tip:{artistId}",
                sessionId: $"cs_tip_{Guid.NewGuid():N}",
                amountTotal: 500)));
    }

    [Fact]
    public async Task ConnectWebhook_AccountMismatch_RemainsFailedAndCreditsNothing()
    {
        var artistId = await SeedArtistAsync(connected: true);
        var eventId = $"evt_mismatch_{Guid.NewGuid():N}";
        var sessionId = $"cs_mismatch_{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
                eventId,
                "checkout.session.completed",
                "acct_wrong_artist",
                clientReferenceId: $"fan-user:tip:{artistId}",
                sessionId: sessionId,
                amountTotal: 500)));

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        Assert.False(await db.EarningsTransactions.AnyAsync(t => t.ExternalRef == sessionId));
        var webhookEvent = await db.StripeWebhookEvents.SingleAsync(e => e.EventId == eventId);
        Assert.Equal("failed", webhookEvent.Status);
        Assert.False(webhookEvent.Processed);
    }

    // ── Self-dealing guard ──

    [Fact]
    public async Task Tip_ToSelf_Returns400()
    {
        var (artistId, artistClient) = await SeedConnectedArtistWithClientAsync();

        var res = await artistClient.PostAsJsonAsync($"/api/artists/{artistId}/tip", new { amountCents = 500 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── Creator support dashboard (GET /api/artists/me/support) ──

    [Fact]
    public async Task Support_ReturnsOwnTipEarnings_Scoped()
    {
        var (artistId, artistClient) = await SeedConnectedArtistWithClientAsync();
        var sessionId = $"cs_tip_{Guid.NewGuid():N}";

        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            "evt_support_tip_" + sessionId, "checkout.session.completed", "acct_fake_123",
            clientReferenceId: $"fan-user-support:tip:{artistId}",
            sessionId: sessionId,
            amountTotal: 700));

        var res = await artistClient.GetAsync("/api/artists/me/support");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(700, data.GetProperty("tipNetCents").GetInt64());     // tips carry no fee
        Assert.Equal(700, data.GetProperty("totalNetCents").GetInt64());
        Assert.Equal(1, data.GetProperty("tipCount").GetInt32());
        Assert.True(data.GetProperty("recent").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Support_DoesNotLeakOtherArtistsEarnings()
    {
        var (artistAId, _) = await SeedConnectedArtistWithClientAsync();
        var (_, artistBClient) = await SeedConnectedArtistWithClientAsync();

        var sessionId = $"cs_tip_{Guid.NewGuid():N}";
        await ProcessConnectEventAsync(svc => svc.ProcessEventAsync(
            "evt_leak_" + sessionId, "checkout.session.completed", "acct_fake_123",
            clientReferenceId: $"fan-leak:tip:{artistAId}",
            sessionId: sessionId,
            amountTotal: 900));

        // Artist B must never see artist A's money-in.
        var res = await artistBClient.GetAsync("/api/artists/me/support");
        var data = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(0, data.GetProperty("totalNetCents").GetInt64());
        Assert.Equal(0, data.GetProperty("tipCount").GetInt32());
    }

    // ── Helpers ──

    private FakePaymentGateway Gateway() =>
        (FakePaymentGateway)_fixture.Services.GetRequiredService<IPaymentGateway>();

    private async Task<HttpClient> CreateFanAsync() =>
        await _fixture.CreateAuthenticatedClientAsync($"fan-{Guid.NewGuid():N}@cambrian.com");

    /// <summary>Seed an artist user; optionally with a connected Stripe account and sub price.</summary>
    private async Task<string> SeedArtistAsync(bool connected, int? subscriptionPriceCents = null)
    {
        var email = $"artist-{Guid.NewGuid():N}@cambrian.com";
        await _fixture.RegisterUserAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.StripeAccountId = connected ? "acct_fake_123" : null;
        user.FanSubscriptionPriceCents = subscriptionPriceCents;
        await db.SaveChangesAsync();
        return userId;
    }

    /// <summary>Register a connected artist AND return an authenticated client for that same user.</summary>
    private async Task<(string userId, HttpClient client)> SeedConnectedArtistWithClientAsync(int? subscriptionPriceCents = null)
    {
        var email = $"artist-self-{Guid.NewGuid():N}@cambrian.com";
        var client = await _fixture.CreateAuthenticatedClientAsync(email);
        var userId = await _fixture.GetUserIdAsync(email);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.StripeAccountId = "acct_fake_123";
        user.FanSubscriptionPriceCents = subscriptionPriceCents;
        await db.SaveChangesAsync();
        return (userId, client);
    }

    private async Task ProcessConnectEventAsync(Func<StripeConnectWebhookService, Task> action)
    {
        using var scope = _fixture.Services.CreateScope();
        var svc = (StripeConnectWebhookService)scope.ServiceProvider.GetRequiredService<IConnectWebhookService>();
        await action(svc);
    }
}
