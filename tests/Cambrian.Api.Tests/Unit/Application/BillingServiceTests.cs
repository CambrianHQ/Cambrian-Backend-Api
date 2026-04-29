using Cambrian.Application.DTOs.Billing;
using Cambrian.Application.DTOs.Subscriptions;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests.Unit.Application;

public sealed class BillingServiceTests
{
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly ISubscriptionService _subscriptionService = Substitute.For<ISubscriptionService>();
    private readonly IPaymentGateway _gateway = Substitute.For<IPaymentGateway>();
    private readonly UserManager<ApplicationUser> _users;
    private readonly BillingService _sut;

    public BillingServiceTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(store, null, null, null, null, null, null, null, null);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:5173"
            })
            .Build();

        _sut = new BillingService(
            _subscriptions,
            _subscriptionService,
            _gateway,
            _users,
            config,
            Substitute.For<ILogger<BillingService>>());
    }

    [Fact]
    public async Task CreateCheckoutAsync_CreatesStripeSubscriptionSession_ForProTier()
    {
        _gateway.CreateSubscriptionCheckoutAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>())
            .Returns("https://checkout.stripe.test/subscription/cs_sub_123");

        var response = await _sut.CreateCheckoutAsync(
            new BillingCheckoutRequest { Tier = "creator" },
            "user-1",
            "buyer@example.com");

        response.CheckoutUrl.Should().Contain("https://checkout.stripe.test/subscription/");

        await _gateway.Received(1).CreateSubscriptionCheckoutAsync(
            Arg.Any<int>(),
            Arg.Any<string>(),
            "user-1:subscription:pro",
            "http://localhost:5173/payment?payment_success=true&session_id={CHECKOUT_SESSION_ID}",
            "http://localhost:5173/payment",
            "buyer@example.com");
    }

    [Fact]
    public async Task ConfirmCheckoutAsync_UpgradesSubscription_WhenPaidSessionBelongsToCaller()
    {
        _gateway.GetCheckoutSessionAsync("cs_sub_123").Returns(new CheckoutSessionInfo
        {
            SessionId = "cs_sub_123",
            Status = "paid",
            ClientReferenceId = "user-77:subscription:pro"
        });
        _subscriptionService.UpdateAsync(Arg.Any<UpdateSubscriptionRequest>(), Arg.Any<string>())
            .Returns(new SubscriptionResponse { Plan = "pro", Status = "active" });

        var response = await _sut.ConfirmCheckoutAsync("cs_sub_123", "user-77");

        response.Status.Should().Be("paid");
        response.Tier.Should().Be("pro");
        await _subscriptionService.Received(1)
            .UpdateAsync(Arg.Is<UpdateSubscriptionRequest>(r => r.Plan == "pro"), "user-77");
    }

    [Fact]
    public async Task CreateCheckoutAsync_PropagatesGatewayTimeout_WithoutMutatingSubscriptions()
    {
        _gateway.CreateSubscriptionCheckoutAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>())
            .Returns<Task<string>>(_ => throw new TimeoutException("Stripe timeout"));

        var act = () => _sut.CreateCheckoutAsync(new BillingCheckoutRequest { Tier = "paid" }, "user-9", "user9@test.com");

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*Stripe timeout*");
        await _subscriptionService.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default!);
    }
}
