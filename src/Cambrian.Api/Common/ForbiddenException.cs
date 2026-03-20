namespace Cambrian.Api.Common;

/// <summary>
/// Thrown when an authenticated user attempts to access a resource they are not authorized for.
/// Maps to HTTP 403 Forbidden (distinct from 401 Unauthorized).
/// </summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message = "Access denied.") : base(message) { }
}
