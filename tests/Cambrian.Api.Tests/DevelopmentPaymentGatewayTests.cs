using Cambrian.Infrastructure.Stripe;
using Microsoft.Extensions.Configuration;

namespace Cambrian.Api.Tests;

public sealed class DevelopmentPaymentGatewayTests
{
    private readonly DevelopmentPaymentGateway _sut;

    public DevelopmentPaymentGatewayTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FrontendUrl"] = "http://localhost:3000"
            })
            .Build();

        _sut = new DevelopmentPaymentGateway(config);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_ReturnsRedirect_WithSyntheticSessionId()
    {
        var url = await _sut.CreateCheckoutSessionAsync(
            2999,
            "Beat",
            clientReferenceId: "user-1:track-1:non-exclusive:personal",
            successUrl: "http://localhost:3000/checkout/success?session_id={CHECKOUT_SESSION_ID}");

        Assert.StartsWith("http://localhost:3000/checkout/success?session_id=cs_dev_", url);
    }

    [Fact]
    public async Task GetCheckoutSessionAsync_ReturnsStoredPaidSession()
    {
        var url = await _sut.CreateCheckoutSessionAsync(
            4999,
            "Beat",
            clientReferenceId: "user-1:track-1:exclusive:personal",
            successUrl: "http://localhost:3000/checkout/success?session_id={CHECKOUT_SESSION_ID}");

        var sessionId = url.Split("session_id=", StringSplitOptions.None)[1];
        var session = await _sut.GetCheckoutSessionAsync(sessionId);

        Assert.NotNull(session);
        Assert.Equal("paid", session!.Status);
        Assert.Equal(4999, session.AmountTotal);
        Assert.Equal("user-1:track-1:exclusive:personal", session.ClientReferenceId);
    }
}
