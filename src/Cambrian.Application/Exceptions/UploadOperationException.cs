namespace Cambrian.Application.Exceptions;

/// <summary>
/// Stable, client-safe upload failure. The internal exception is retained for
/// structured server diagnostics but its message is never returned to callers.
/// </summary>
public sealed class UploadOperationException : Exception
{
    public UploadOperationException(string code, string message, string category, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        Category = category;
    }

    /// <summary>
    /// For failures that aren't wrapping an underlying exception (idempotency
    /// conflicts, duplicate-audio detection) and carry extra structured fields
    /// the controller should surface to the client (e.g. an existing track id).
    /// </summary>
    public UploadOperationException(string code, string message, string category, IReadOnlyDictionary<string, object?>? extra = null)
        : base(message)
    {
        Code = code;
        Category = category;
        Extra = extra;
    }

    public string Code { get; }
    public string Category { get; }
    public IReadOnlyDictionary<string, object?>? Extra { get; }
}
