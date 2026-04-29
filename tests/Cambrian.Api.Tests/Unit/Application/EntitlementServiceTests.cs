using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cambrian.Api.Tests.Unit.Application;

public sealed class EntitlementServiceTests
{
    [Fact]
    public async Task CanDownloadAsync_ReturnsFalse_WhenRepositoryHasNoCompletedPurchase()
    {
        var trackId = Guid.NewGuid();
        var purchases = Substitute.For<IPurchaseRepository>();
        purchases.HasCompletedPurchaseAsync("buyer-1", trackId).Returns(false);

        var sut = new EntitlementService(
            purchases,
            Substitute.For<IEntitlementRepository>(),
            NullLogger<EntitlementService>.Instance);

        var allowed = await sut.CanDownloadAsync("buyer-1", trackId);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CanDownloadAsync_ReturnsTrue_WhenRepositoryHasCompletedPurchase()
    {
        var trackId = Guid.NewGuid();
        var purchases = Substitute.For<IPurchaseRepository>();
        purchases.HasCompletedPurchaseAsync("buyer-1", trackId).Returns(true);

        var sut = new EntitlementService(
            purchases,
            Substitute.For<IEntitlementRepository>(),
            NullLogger<EntitlementService>.Instance);

        var allowed = await sut.CanDownloadAsync("buyer-1", trackId);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanDownloadAsync_DelegatesToRepository_WithExactLookupKey()
    {
        var trackId = Guid.NewGuid();
        var purchases = Substitute.For<IPurchaseRepository>();
        var sut = new EntitlementService(
            purchases,
            Substitute.For<IEntitlementRepository>(),
            NullLogger<EntitlementService>.Instance);

        await sut.CanDownloadAsync("buyer-42", trackId);

        await purchases.Received(1).HasCompletedPurchaseAsync("buyer-42", trackId);
    }
}
