using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Cambrian.Api.Security;

public static class ForwardedHeaderConfiguration
{
    public static bool HasTrustedForwarders(IConfiguration configuration) =>
        Split(configuration["ForwardedHeaders:KnownProxies"]).Any()
        || Split(configuration["ForwardedHeaders:KnownNetworks"]).Any();

    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var value in Split(configuration["ForwardedHeaders:KnownProxies"]))
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException($"Invalid trusted proxy IP: {value}");
            options.KnownProxies.Add(address);
        }

        foreach (var value in Split(configuration["ForwardedHeaders:KnownNetworks"]))
        {
            var parts = value.Split('/', 2);
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var prefix)
                || !int.TryParse(parts[1], out var prefixLength))
            {
                throw new InvalidOperationException($"Invalid trusted proxy network: {value}");
            }

            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
        }

        if (options.KnownNetworks.Count == 0 && options.KnownProxies.Count == 0)
            throw new InvalidOperationException("At least one trusted proxy or network must be configured.");
    }

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
