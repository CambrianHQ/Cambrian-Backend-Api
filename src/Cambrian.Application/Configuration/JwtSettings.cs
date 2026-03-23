namespace Cambrian.Application.Configuration;

public sealed class JwtSettings
{
    public string Key { get; init; } = string.Empty;

    public string Issuer { get; init; } = "cambrian-api";

    public string Audience { get; init; } = "cambrian-client";
}