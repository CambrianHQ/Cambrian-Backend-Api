using Cambrian.Application.Interfaces.V1;
using Cambrian.Persistence;
using Cambrian.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Tests;

public sealed class UploadIdempotencyStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CambrianDbContext _db;
    private readonly IdempotencyStore _store;

    public UploadIdempotencyStoreTests()
    {
        _connection.Open();
        _db = new CambrianDbContext(new DbContextOptionsBuilder<CambrianDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _store = new IdempotencyStore(_db);
    }

    [Fact]
    public async Task SameKeyAndPayload_ReplaysCompletedResponse_WithoutSecondClaim()
    {
        var first = await _store.TryBeginAsync("key-1", "user-1", "POST /upload", "HASH-A");
        Assert.Equal(IdempotencyClaimOutcome.Claimed, first.Outcome);
        await _store.CompleteAsync("key-1", "user-1", "POST /upload", 201, "{\"trackId\":\"one\"}");

        var replay = await _store.TryBeginAsync("key-1", "user-1", "POST /upload", "HASH-A");

        Assert.Equal(IdempotencyClaimOutcome.Completed, replay.Outcome);
        Assert.Equal(201, replay.StatusCode);
        Assert.Equal("{\"trackId\":\"one\"}", replay.ResponseBody);
        Assert.Single(_db.ApiIdempotencyKeys);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentPayload_IsRejected()
    {
        await _store.TryBeginAsync("key-2", "user-1", "POST /upload", "HASH-A");

        var mismatch = await _store.TryBeginAsync("key-2", "user-1", "POST /upload", "HASH-B");

        Assert.Equal(IdempotencyClaimOutcome.Mismatch, mismatch.Outcome);
    }

    [Fact]
    public async Task FailedClaim_CanBeRetried_WithoutCreatingSecondRow()
    {
        await _store.TryBeginAsync("key-3", "user-1", "POST /upload", "HASH-A");
        await _store.MarkFailedAsync("key-3", "user-1", "POST /upload");

        var retry = await _store.TryBeginAsync("key-3", "user-1", "POST /upload", "HASH-A");

        Assert.Equal(IdempotencyClaimOutcome.Claimed, retry.Outcome);
        Assert.Single(_db.ApiIdempotencyKeys);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
