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

    Task CommitAsync();
    Task RollbackAsync();
}
