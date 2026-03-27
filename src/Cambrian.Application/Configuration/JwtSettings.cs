namespace Cambrian.Application.Configuration;

public sealed class JwtSettings
{
    public string Key { get; init; } = string.Empty;

    public string Issuer { get; init; } = "cambrian-api";

    public string Audience { get; init; } = "cambrian-client";

    /// <summary>Token lifetime in minutes. Defaults to 120 (2 hours). Configurable via Jwt:ExpirationMinutes.</summary>
    public int ExpirationMinutes { get; init; } = 120;
}