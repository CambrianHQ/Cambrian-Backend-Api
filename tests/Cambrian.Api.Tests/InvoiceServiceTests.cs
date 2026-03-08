using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class InvoiceServiceTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly InvoiceService _sut;

    public InvoiceServiceTests()
    {
        _sut = new InvoiceService(_invoices);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenInvoiceIdNotGuid()
    {
        var result = await _sut.GetByIdAsync("not-a-guid", "user-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenInvoiceNotFound()
    {
        var id = Guid.NewGuid();
        _invoices.GetByIdAsync(id).Returns((Invoice?)null);

        var result = await _sut.GetByIdAsync(id.ToString(), "user-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenUserDoesNotOwnInvoice()
    {
        var id = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = id,
            UserId = "other-user",
            PurchaseId = Guid.NewGuid(),
            AmountCents = 2999,
            Status = "paid"
        };
        _invoices.GetByIdAsync(id).Returns(invoice);

        var result = await _sut.GetByIdAsync(id.ToString(), "user-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsInvoice_WhenUserOwnsIt()
    {
        var id = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var invoice = new Invoice
        {
            Id = id,
            UserId = "user-1",
            PurchaseId = purchaseId,
            AmountCents = 4999,
            Currency = "usd",
            Status = "paid",
            IssuedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            PaidAt = new DateTime(2025, 6, 1, 0, 1, 0, DateTimeKind.Utc)
        };
        _invoices.GetByIdAsync(id).Returns(invoice);

        var result = await _sut.GetByIdAsync(id.ToString(), "user-1");

        Assert.NotNull(result);
        Assert.Equal(id.ToString(), result!.Id);
        Assert.Equal(purchaseId.ToString(), result.PurchaseId);
        Assert.Equal(4999, result.AmountCents);
        Assert.Equal("usd", result.Currency);
        Assert.Equal("paid", result.Status);
    }

    [Fact]
    public async Task GetByUserAsync_ReturnsEmptyList_WhenNoInvoices()
    {
        _invoices.GetByUserIdAsync("user-1").Returns(new List<Invoice>());

        var result = await _sut.GetByUserAsync("user-1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByUserAsync_MapsAllInvoices()
    {
        _invoices.GetByUserIdAsync("user-1").Returns(new List<Invoice>
        {
            new() { Id = Guid.NewGuid(), UserId = "user-1", PurchaseId = Guid.NewGuid(), AmountCents = 1000, Currency = "usd", Status = "paid" },
            new() { Id = Guid.NewGuid(), UserId = "user-1", PurchaseId = Guid.NewGuid(), AmountCents = 2000, Currency = "usd", Status = "issued" }
        });

        var result = await _sut.GetByUserAsync("user-1");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsNull()
    {
        var result = await _sut.DownloadAsync(Guid.NewGuid().ToString(), "user-1");

        Assert.Null(result);
    }
}
