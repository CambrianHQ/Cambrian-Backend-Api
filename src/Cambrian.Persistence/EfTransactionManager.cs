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
