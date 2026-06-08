namespace Cambrian.Application.Exceptions;

/// <summary>
/// Thrown when an action is blocked by the caller's subscription plan (e.g. the Free
/// track-hosting limit is reached). Maps to HTTP 402 Payment Required and carries a
/// stable machine-readable <see cref="Code"/> the frontend uses to trigger an upgrade flow.
/// </summary>
public sealed class UpgradeRequiredException : Exception
{
    /// <summary>Stable code the frontend detects to prompt an upgrade.</summary>
    public const string DefaultCode = "UPGRADE_REQUIRED";

    public string Code { get; }

    public UpgradeRequiredException(string message, string code = DefaultCode) : base(message)
    {
        Code = code;
    }
}
