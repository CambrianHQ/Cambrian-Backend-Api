using Cambrian.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cambrian.Persistence;

public sealed class EfTransactionManager : ITransactionManager
{
    private readonly CambrianDbContext _db;
    private IDbContextTransaction? _transaction;

    public EfTransactionManager(CambrianDbContext db) => _db = db;

    public async Task<IAsyncDisposable> BeginTransactionAsync()
    {
        _transaction = await _db.Database.BeginTransactionAsync();
        return _transaction;
    }

    public async Task<IAsyncDisposable> BeginSerializableTransactionAsync()
    {
        _transaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        return _transaction;
    }

    /// <summary>
    /// PostgreSQL: takes a transaction-scoped advisory lock (<c>pg_advisory_xact_lock</c>) whose id
    /// is a stable hash of <paramref name="key"/>, so concurrent transactions using the same key are
    /// forced to run one at a time and release automatically at COMMIT/ROLLBACK. Other providers
    /// (SQLite) have no advisory locks and already serialize writers, so this is a no-op there.
    /// </summary>
    public async Task AcquireAdvisoryLockAsync(string key, CancellationToken ct = default)
    {
        if (!_db.Database.IsNpgsql())
            return;

        // hashtextextended(text, seed) -> bigint maps the key into the advisory-lock id space.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    public async Task CommitAsync()
    {
        if (_transaction is not null)
            await _transaction.CommitAsync();
    }

    public async Task RollbackAsync()
    {
        if (_transaction is not null)
            await _transaction.RollbackAsync();
    }
}
