namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstracts database transaction lifecycle for the Application layer.
/// </summary>
public interface ITransactionManager
{
    Task<IAsyncDisposable> BeginTransactionAsync();

    /// <summary>
    /// Begins a Serializable-isolation transaction.
    /// Use for operations that must prevent phantom reads / double-withdrawal races.
    /// </summary>
    Task<IAsyncDisposable> BeginSerializableTransactionAsync();

    /// <summary>
    /// Acquires a transaction-scoped mutual-exclusion lock keyed by an arbitrary string,
    /// released automatically when the surrounding transaction commits or rolls back. Call
    /// it as the first statement inside a transaction to serialize a read-then-write against
    /// a DERIVED aggregate (e.g. "count charged this month") that has no single row to lock —
    /// serializable isolation alone does not reliably abort that write-skew on PostgreSQL.
    /// Backed by <c>pg_advisory_xact_lock</c> on PostgreSQL; a no-op on providers without
    /// advisory locks (SQLite already serializes writers), so it must be paired with a
    /// transaction, never relied on as the sole guard on non-PostgreSQL providers.
    /// </summary>
    Task AcquireAdvisoryLockAsync(string key, CancellationToken ct = default);

    Task CommitAsync();
    Task RollbackAsync();
}
