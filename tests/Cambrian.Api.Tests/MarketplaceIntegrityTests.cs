using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

public sealed class MarketplaceIntegrityTests : IDisposable
{
    private readonly CambrianDbContext _db;
    private readonly MarketplaceIntegrityService _sut;

    public MarketplaceIntegrityTests()
    {
        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new CambrianDbContext(options);
        _sut = new MarketplaceIntegrityService(
            _db,
            Substitute.For<ILogger<MarketplaceIntegrityService>>());
    }

    public void Dispose() => _db.Dispose();

    // ── Rule: purchase.completed → library entry must exist ──

    [Fact]
    public async Task Audit_DetectsCompletedPurchaseWithoutLibraryEntry()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = "c1" });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 10,
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Contains(report.Violations, v => v.Rule == "purchase-library-link");
        Assert.True(report.Summary.CompletedPurchasesWithoutLibrary > 0);
    }

    [Fact]
    public async Task Audit_NoViolation_WhenCompletedPurchaseHasLibraryEntry()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = "c1" });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 10,
            Status = "completed"
        });
        _db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = "buyer-1",
            TrackId = trackId,
            Title = "Beat"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.DoesNotContain(report.Violations, v => v.Rule == "purchase-library-link");
    }

    // ── Rule: exclusiveSold = true → track must not be browsable ──

    [Fact]
    public async Task Audit_DetectsExclusiveSoldTrackStillPublic()
    {
        _db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            Title = "Exclusive Beat",
            CreatorId = "c1",
            ExclusiveSold = true,
            Visibility = "public"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Contains(report.Violations, v => v.Rule == "exclusive-visibility");
        Assert.True(report.Summary.ExclusiveSoldButBrowsable > 0);
    }

    [Fact]
    public async Task Audit_NoViolation_WhenExclusiveSoldTrackIsHidden()
    {
        _db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            Title = "Exclusive Beat",
            CreatorId = "c1",
            ExclusiveSold = true,
            Visibility = "hidden"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.DoesNotContain(report.Violations, v => v.Rule == "exclusive-visibility");
    }

    // ── Rule: creator payout must not exceed purchase revenue ──

    [Fact]
    public async Task Audit_DetectsPayoutExceedingRevenue()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = "creator-1" });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 50,
            Status = "completed"
        });
        _db.Payouts.Add(new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = "creator-1",
            Amount = 100,
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Contains(report.Violations, v => v.Rule == "payout-revenue-match");
        Assert.True(report.Summary.PayoutAmountMismatches > 0);
    }

    [Fact]
    public async Task Audit_NoViolation_WhenPayoutWithinRevenue()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = "creator-1" });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 100,
            Status = "completed"
        });
        _db.Payouts.Add(new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = "creator-1",
            Amount = 80,
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.DoesNotContain(report.Violations,
            v => v.Rule == "payout-revenue-match" && v.Severity == "error"
                 && v.EntityId == "creator-1");
    }

    [Fact]
    public async Task Audit_DetectsPayoutWithEmptyCreatorId()
    {
        _db.Payouts.Add(new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = "",
            Amount = 50,
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Contains(report.Violations,
            v => v.Rule == "payout-revenue-match" && v.Description.Contains("empty CreatorId"));
    }

    // ── Rule: completed purchases should have invoices ──

    [Fact]
    public async Task Audit_DetectsCompletedPurchaseWithoutInvoice()
    {
        var trackId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        _db.Tracks.Add(new Track { Id = trackId, Title = "Beat", CreatorId = "c1" });
        _db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 10,
            Status = "completed"
        });
        _db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = "buyer-1",
            TrackId = trackId,
            Title = "Beat"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Contains(report.Violations, v => v.Rule == "purchase-invoice-link");
        Assert.True(report.Summary.PurchasesWithoutInvoice > 0);
    }

    // ── Rule: exclusive purchase → ExclusiveSold flag must be true ──

    [Fact]
    public async Task Audit_DetectsExclusivePurchaseWithoutTrackFlag()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = "Beat",
            CreatorId = "c1",
            ExclusiveSold = false
        });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 500,
            LicenseType = "exclusive",
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Contains(report.Violations, v => v.Rule == "exclusive-purchase-flag");
        Assert.True(report.Summary.ExclusivePurchasesWithoutFlag > 0);
    }

    [Fact]
    public async Task Audit_NoViolation_WhenExclusivePurchaseHasTrackFlag()
    {
        var trackId = Guid.NewGuid();
        _db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = "Beat",
            CreatorId = "c1",
            ExclusiveSold = true
        });
        _db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 500,
            LicenseType = "exclusive",
            Status = "completed"
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.DoesNotContain(report.Violations, v => v.Rule == "exclusive-purchase-flag");
    }

    // ── Clean state produces zero violations ──

    [Fact]
    public async Task Audit_ReturnsCleanReport_WhenNoData()
    {
        var report = await _sut.RunAuditAsync();

        Assert.Equal(0, report.TotalViolations);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public async Task Audit_ReturnsCleanReport_WhenDataIsConsistent()
    {
        var trackId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();

        _db.Tracks.Add(new Track
        {
            Id = trackId,
            Title = "Beat",
            CreatorId = "c1",
            ExclusiveSold = false,
            Visibility = "public"
        });
        _db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            BuyerId = "buyer-1",
            TrackId = trackId,
            Amount = 10,
            LicenseType = "non-exclusive",
            Status = "completed"
        });
        _db.Library.Add(new LibraryItem
        {
            Id = Guid.NewGuid(),
            UserId = "buyer-1",
            TrackId = trackId,
            Title = "Beat"
        });
        _db.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            UserId = "buyer-1",
            PurchaseId = purchaseId,
            AmountCents = 1000,
            Currency = "usd",
            Status = "paid",
            IssuedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var report = await _sut.RunAuditAsync();

        Assert.Equal(0, report.TotalViolations);
    }
}
