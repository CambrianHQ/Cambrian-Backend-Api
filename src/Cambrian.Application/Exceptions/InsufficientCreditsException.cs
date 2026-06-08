namespace Cambrian.Application.Exceptions;

/// <summary>
/// Thrown when a creator with no remaining Release Ready credits attempts to
/// submit (ffmpeg) or approve (Tonn) a master. The controller maps this to
/// HTTP 403 with error code <c>insufficient_credits</c>.
/// </summary>
public sealed class InsufficientCreditsException : Exception
{
    public InsufficientCreditsException(string message = "No Release Ready credits remaining this month.")
        : base(message) { }
}
