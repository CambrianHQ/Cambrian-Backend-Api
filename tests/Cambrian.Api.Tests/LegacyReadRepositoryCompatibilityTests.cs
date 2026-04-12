using Cambrian.Domain.Entities;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Tests;

public sealed class LegacyReadRepositoryCompatibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CambrianDbContext _db;
    private readonly PurchaseRepository _purchases;
    private readonly LibraryRepository _library;
    private readonly InvoiceRepository _invoices;

    public LegacyReadRepositoryCompatibilityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new CambrianDbContext(options);
        _purchases = new PurchaseRepository(_db);
        _library = new LibraryRepository(_db);
        _invoices = new InvoiceRepository(_db);

        CreateLegacySchema();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task PurchaseRepository_GetByBuyerIdAsync_WorksAgainstLegacyTrackSchema()
    {
        var purchaseId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        InsertLegacyTrack(trackId, "creator-1", createdAt);
        ExecuteNonQuery($$"""
            INSERT INTO "Purchases" ("Id", "BuyerId", "TrackId", "AmountCents", "PaymentMethod", "LicenseType", "Status", "UsageType", "StripeSessionId", "LicenseId", "CompletedAt", "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES ('{{purchaseId}}', 'buyer-1', '{{trackId}}', 2999, 'stripe', 'non-exclusive', 'completed', 'youtube', 'cs_test_123', NULL, '{{createdAt:O}}', NULL, '{{createdAt:O}}', NULL);
            """);

        var purchases = await _purchases.GetByBuyerIdAsync("buyer-1");

        var purchase = Assert.Single(purchases);
        Assert.Equal(purchaseId, purchase.Id);
        Assert.Equal(trackId, purchase.TrackId);
        Assert.Equal(2999, purchase.AmountCents);
        Assert.Equal("completed", purchase.Status);
    }

    [Fact]
    public async Task LibraryRepository_GetByUserIdAsync_WorksAgainstLegacyTrackSchema()
    {
        var trackId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        ExecuteNonQuery(
            """
            INSERT INTO "AspNetUsers" ("Id", "UserName", "DisplayName", "ProfileImageUrl")
            VALUES ('creator-1', 'logan', 'Logan', 'profiles/logan.jpg');
            """);
        InsertLegacyTrack(trackId, "creator-1", createdAt, "Legacy Beat");
        ExecuteNonQuery($$"""
            INSERT INTO "Library" ("Id", "UserId", "TrackId", "PurchaseId", "Title", "Artist", "AudioUrl", "SavedAt")
            VALUES ('{{libraryId}}', 'buyer-1', '{{trackId}}', NULL, 'Saved Title', 'Saved Artist', 'saved.mp3', '{{createdAt:O}}');
            """);

        var items = await _library.GetByUserIdAsync("buyer-1");

        var item = Assert.Single(items);
        Assert.Equal("Saved Title", item.Title);
        Assert.NotNull(item.Track);
        Assert.Equal("Legacy Beat", item.Track.Title);
        Assert.Equal("Trap", item.Track.Genre);
        Assert.NotNull(item.Track.Creator);
        Assert.Equal("Logan", item.Track.Creator.DisplayName);
    }

    [Fact]
    public async Task InvoiceRepository_GetByUserIdAsync_WorksAgainstLegacyTrackSchema()
    {
        var purchaseId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        InsertLegacyTrack(trackId, "creator-1", createdAt);
        ExecuteNonQuery($$"""
            INSERT INTO "Purchases" ("Id", "BuyerId", "TrackId", "AmountCents", "PaymentMethod", "LicenseType", "Status", "UsageType", "StripeSessionId", "LicenseId", "CompletedAt", "ExpiresAt", "CreatedAt", "UpdatedAt")
            VALUES ('{{purchaseId}}', 'buyer-1', '{{trackId}}', 4999, 'stripe', 'exclusive', 'completed', 'film', 'cs_test_invoice', NULL, '{{createdAt:O}}', NULL, '{{createdAt:O}}', NULL);
            """);
        ExecuteNonQuery($$"""
            INSERT INTO "Invoices" ("Id", "UserId", "PurchaseId", "AmountCents", "Currency", "Status", "IssuedAt", "PaidAt")
            VALUES ('{{invoiceId}}', 'buyer-1', '{{purchaseId}}', 4999, 'usd', 'paid', '{{createdAt:O}}', '{{createdAt:O}}');
            """);

        var invoices = await _invoices.GetByUserIdAsync("buyer-1");

        var invoice = Assert.Single(invoices);
        Assert.Equal(invoiceId, invoice.Id);
        Assert.Equal(purchaseId, invoice.PurchaseId);
        Assert.Equal(4999, invoice.AmountCents);
        Assert.Equal("paid", invoice.Status);
    }

    private void CreateLegacySchema()
    {
        ExecuteNonQuery(
            """
            CREATE TABLE "AspNetUsers" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "UserName" TEXT NULL,
                "DisplayName" TEXT NULL,
                "ProfileImageUrl" TEXT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "Tracks" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "CambrianTrackId" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NULL,
                "Genre" TEXT NULL,
                "Price" REAL NOT NULL,
                "NonExclusivePriceCents" INTEGER NOT NULL,
                "ExclusivePriceCents" INTEGER NOT NULL,
                "CopyrightBuyoutPriceCents" INTEGER NOT NULL,
                "ExclusiveSold" INTEGER NOT NULL,
                "Status" TEXT NOT NULL,
                "CopyrightOwnerId" TEXT NULL,
                "LicenseType" TEXT NULL,
                "Duration" TEXT NULL,
                "AudioUrl" TEXT NULL,
                "CoverArtUrl" TEXT NULL,
                "CreatorId" TEXT NOT NULL,
                "CreatorUuid" TEXT NULL,
                "Visibility" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "Purchases" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "BuyerId" TEXT NOT NULL,
                "TrackId" TEXT NOT NULL,
                "AmountCents" INTEGER NOT NULL,
                "PaymentMethod" TEXT NULL,
                "LicenseType" TEXT NULL,
                "Status" TEXT NOT NULL,
                "UsageType" TEXT NOT NULL,
                "StripeSessionId" TEXT NULL,
                "LicenseId" TEXT NULL,
                "CompletedAt" TEXT NULL,
                "ExpiresAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "Library" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "TrackId" TEXT NOT NULL,
                "PurchaseId" TEXT NULL,
                "Title" TEXT NULL,
                "Artist" TEXT NULL,
                "AudioUrl" TEXT NULL,
                "SavedAt" TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(
            """
            CREATE TABLE "Invoices" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "PurchaseId" TEXT NOT NULL,
                "AmountCents" INTEGER NOT NULL,
                "Currency" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "IssuedAt" TEXT NOT NULL,
                "PaidAt" TEXT NULL
            );
            """);
    }

    private void InsertLegacyTrack(Guid trackId, string creatorId, DateTime createdAt, string title = "Legacy Track")
    {
        ExecuteNonQuery($$"""
            INSERT INTO "Tracks" ("Id", "CambrianTrackId", "Title", "Description", "Genre", "Price", "NonExclusivePriceCents", "ExclusivePriceCents", "CopyrightBuyoutPriceCents", "ExclusiveSold", "Status", "CopyrightOwnerId", "LicenseType", "Duration", "AudioUrl", "CoverArtUrl", "CreatorId", "CreatorUuid", "Visibility", "CreatedAt")
            VALUES ('{{trackId}}', 'CAMB-TRK-LEGACY02', '{{title}}', 'compat', 'Trap', 29.99, 2999, 4999, 0, 0, 'available', NULL, 'non-exclusive', '3:10', 'audio.mp3', 'cover.jpg', '{{creatorId}}', NULL, 'public', '{{createdAt:O}}');
            """);
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
