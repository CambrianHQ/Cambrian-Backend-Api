using System.Net;
using System.Net.Http.Json;
using Cambrian.Api.Tests.Fixtures;
using Cambrian.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Cambrian.Api.Tests.Integration.Api;

public sealed class FailureModeApiTests : IClassFixture<RelationalCambrianApiFixture>
{
    private readonly RelationalCambrianApiFixture _fixture;

    public FailureModeApiTests(RelationalCambrianApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Checkout_WhenStripeTimesOut_ReturnsControlledError_AndPersistsNothing()
    {
        await _fixture.RegisterUserAsync("failure-creator@cambrian.com");
        var creatorId = await _fixture.GetUserIdAsync("failure-creator@cambrian.com");
        var trackId = await _fixture.SeedTrackAsync(creatorId, "Failure Beat");
        var buyer = await _fixture.CreateAuthenticatedClientAsync("failure-buyer@cambrian.com");

        _fixture.PaymentGateway.FailNextCreate(new TimeoutException("Stripe timeout"));

        var response = await buyer.PostAsJsonAsync("/checkout", new
        {
            trackId = trackId.ToString(),
            licenseType = "non-exclusive",
            usageType = "personal"
        });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var buyerId = await _fixture.GetUserIdAsync("failure-buyer@cambrian.com");
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CambrianDbContext>();

        db.Purchases.Should().NotContain(p => p.BuyerId == buyerId && p.TrackId == trackId);
        db.Library.Should().NotContain(l => l.UserId == buyerId && l.TrackId == trackId);
    }
}
