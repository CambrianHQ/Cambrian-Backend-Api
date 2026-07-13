using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Cambrian.Infrastructure.Stripe;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests.Webhooks;

public sealed class StripeWebhookLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;
    private readonly StripeWebhookService _sut;

    public StripeWebhookLifecycleTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new CambrianDbContext(options);
        _db.Database.EnsureCreated();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:WebhookSecret"] = "whsec_test",
                ["App:FrontendUrl"] = "http://localhost:5173"
            })
            .Build();

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Testing");

        _sut = new StripeWebhookService(
            _db,
            Substitute.For<IEmailService>(),
            config,
            Substitute.For<ILogger<StripeWebhookService>>(),
            env);
    }

    [Fact]
    public async Task InvoicePaid_RestoresSubscriptionHealth_FromLocalCustomerMapping()
    {
        var user = new ApplicationUser
        {
            Id = "user-paid",
            Email = "paid@test.com",
            NormalizedEmail = "PAID@TEST.COM",
            UserName = "paid@test.com",
            Tier = "pro",
            SubscriptionStatus = "PastDue"
        };
        _db.Users.Add(user);
        _db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Plan = "pro",
            Status = "active",
            StripeCustomerId = "cus_local_paid",
            StripeSubscriptionId = "sub_local_paid",
            StartedAt = DateTime.UtcNow.AddDays(-3),
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        await _sut.ProcessEventAsync(
            eventId: "evt_invoice_paid_local",
            eventType: "invoice.paid",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: "cus_local_paid",
            stripeSessionId: null,
            stripeSubscriptionId: "sub_local_paid",
            invoiceId: "in_local_paid",
            invoicePeriodEnd: DateTime.UtcNow.AddMonths(1));

        var refreshedUser = await _db.Users.FindAsync(user.Id);
        var refreshedSub = await _db.Subscriptions.SingleAsync();

        refreshedUser!.SubscriptionStatus.Should().Be("Active");
        refreshedSub.Status.Should().Be("active");
        refreshedSub.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task InvoicePaymentFailed_MarksUserPastDue_FromLocalCustomerMapping()
    {
        var user = new ApplicationUser
        {
            Id = "user-failed",
            Email = "failed@test.com",
            NormalizedEmail = "FAILED@TEST.COM",
            UserName = "failed@test.com",
            SubscriptionStatus = "Active"
        };
        _db.Users.Add(user);
        _db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Plan = "pro",
            Status = "active",
            StripeCustomerId = "cus_local_failed",
            StripeSubscriptionId = "sub_local_failed"
        });
        await _db.SaveChangesAsync();

        await _sut.ProcessEventAsync(
            eventId: "evt_invoice_failed_local",
            eventType: "invoice.payment_failed",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: "cus_local_failed",
            stripeSessionId: null,
            stripeSubscriptionId: "sub_local_failed",
            invoiceId: "in_local_failed");

        (await _db.Users.FindAsync(user.Id))!.SubscriptionStatus.Should().Be("PastDue");
        (await _db.Subscriptions.SingleAsync()).Status.Should().Be("past_due");
        (await _db.Users.FindAsync(user.Id))!.Tier.Should().Be("free");
    }

    [Fact]
    public async Task PaymentIntentSucceeded_IsRecordedIdempotently_WithoutBusinessSideEffects()
    {
        await _sut.ProcessEventAsync(
            eventId: "evt_pi_succeeded",
            eventType: "payment_intent.succeeded",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null,
            stripePaymentIntentId: "pi_test_123");

        await _sut.ProcessEventAsync(
            eventId: "evt_pi_succeeded",
            eventType: "payment_intent.succeeded",
            clientReferenceId: null,
            amountTotal: null,
            stripeCustomerId: null,
            stripeSessionId: null,
            stripePaymentIntentId: "pi_test_123");

        _db.StripeWebhookEvents.Should().ContainSingle(e =>
            e.EventId == "evt_pi_succeeded" &&
            e.Status == "completed");
        _db.Purchases.Should().BeEmpty();
        _db.Subscriptions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("charge.refunded", "refunded")]
    [InlineData("charge.dispute.created", "disputed")]
    public async Task CreditPackRefundOrDispute_RemovesCreditsFromAuthoritativePaidPool(string eventType, string expectedStatus)
    {
        var user = new ApplicationUser
        {
            Id = $"user-{expectedStatus}", Email = $"{expectedStatus}@test.com",
            NormalizedEmail = $"{expectedStatus}@test.com".ToUpperInvariant(), UserName = $"{expectedStatus}@test.com"
        };
        _db.Users.Add(user);
        _db.ReleaseCreditPurchases.Add(new ReleaseCreditPurchase
        {
            Id = Guid.NewGuid(), CreatorId = user.Id, Credits = 10, AmountCents = 6900,
            Pack = "ten", Status = "paid", StripeSessionId = $"cs_{expectedStatus}",
            StripePaymentIntentId = $"pi_{expectedStatus}", CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await _sut.ProcessEventAsync(
            eventId: $"evt_{expectedStatus}", eventType: eventType,
            clientReferenceId: null, amountTotal: null, stripeCustomerId: null,
            stripeSessionId: null, stripePaymentIntentId: $"pi_{expectedStatus}");

        var purchase = await _db.ReleaseCreditPurchases.SingleAsync();
        purchase.Status.Should().Be(expectedStatus);
        (await _db.ReleaseCreditPurchases.Where(x => x.Status == "paid").SumAsync(x => (int?)x.Credits) ?? 0)
            .Should().Be(0);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
