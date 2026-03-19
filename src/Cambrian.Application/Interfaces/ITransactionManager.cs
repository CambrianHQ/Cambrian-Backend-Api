namespace Cambrian.Application.Interfaces;

/// <summary>
/// Abstracts database transaction lifecycle for the Application layer.
/// </summary>
public interface ITransactionManager
{
    Task<IAsyncDisposable> BeginTransactionAsync();
    Task CommitAsync();
    Task RollbackAsync();
}
