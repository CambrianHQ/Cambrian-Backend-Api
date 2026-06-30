using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cambrian.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cambrian.Api.Tests.Contract;

public sealed partial class EndpointManifestSecurityTests : IClassFixture<CambrianApiFixture>
{
    private readonly CambrianApiFixture _fixture;

    public EndpointManifestSecurityTests(CambrianApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CheckedInManifest_DoesNotMarkProtectedControllerRoutesPublic()
    {
        var manifest = LoadCheckedInManifest();
        var manifestByKey = manifest.Endpoints.ToDictionary(
            endpoint => $"{endpoint.Method} {endpoint.Path}",
            StringComparer.OrdinalIgnoreCase);

        foreach (var route in GetControllerRouteSecurity().Where(route => route.RequiresAuth))
        {
            var key = $"{route.Method} {route.Path}";
            if (!manifestByKey.TryGetValue(key, out var manifestEndpoint))
                continue;

            Assert.True(
                manifestEndpoint.RequiresAuth,
                $"Endpoint manifest marks protected route public: {key}");

            if (!string.IsNullOrWhiteSpace(route.RequiresRole))
            {
                Assert.Equal(
                    NormalizeCsv(route.RequiresRole),
                    NormalizeCsv(manifestEndpoint.RequiresRole));
            }
        }
    }

    [Fact]
    public async Task RuntimeManifest_UsesEndpointAuthorizationMetadata()
    {
        using var client = _fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var manifest = await client.GetFromJsonAsync<EndpointManifest>("/manifest.json");
        Assert.NotNull(manifest);

        var endpoints = manifest!.Endpoints.ToDictionary(
            endpoint => $"{endpoint.Method} {endpoint.Path}",
            StringComparer.OrdinalIgnoreCase);

        AssertProtected(endpoints, "POST", "/api/billing/checkout");
        AssertProtected(endpoints, "GET", "/api/me/entitlements");
        AssertProtected(endpoints, "GET", "/feature-flags", "Admin");
        AssertProtected(endpoints, "GET", "/health/details", "Admin");
        AssertProtected(endpoints, "POST", "/release-ready/credits/checkout");
        AssertProtected(endpoints, "POST", "/stream/start");

        AssertPublic(endpoints, "POST", "/auth/login");
        AssertPublic(endpoints, "GET", "/health");
        AssertPublic(endpoints, "POST", "/webhook/stripe");
    }

    private static void AssertProtected(
        IReadOnlyDictionary<string, EndpointManifestEntry> endpoints,
        string method,
        string path,
        string? role = null)
    {
        var endpoint = AssertEndpoint(endpoints, method, path);
        Assert.True(endpoint.RequiresAuth, $"{method} {path} must require auth in /manifest.json");

        if (!string.IsNullOrWhiteSpace(role))
            Assert.Contains(role, endpoint.RequiresRole ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPublic(
        IReadOnlyDictionary<string, EndpointManifestEntry> endpoints,
        string method,
        string path)
    {
        var endpoint = AssertEndpoint(endpoints, method, path);
        Assert.False(endpoint.RequiresAuth, $"{method} {path} must remain public in /manifest.json");
    }

    private static EndpointManifestEntry AssertEndpoint(
        IReadOnlyDictionary<string, EndpointManifestEntry> endpoints,
        string method,
        string path)
    {
        var key = $"{method} {path}";
        Assert.True(endpoints.TryGetValue(key, out var endpoint), $"Missing endpoint from manifest: {key}");
        return endpoint!;
    }

    private static EndpointManifest LoadCheckedInManifest()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "endpoint-manifest.v1.json"));
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<EndpointManifest>(json, JsonOptions)!;
    }

    private static IEnumerable<RouteSecurity> GetControllerRouteSecurity()
    {
        var assembly = Assembly.Load("Cambrian.Api");
        var controllers = assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        foreach (var controller in controllers)
        {
            if (IsIgnored(controller))
                continue;

            var routePrefix = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? "";
            var classAuthorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToArray();
            var classAllowAnonymous = controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
            var classRequiresCreatorTier = RequiresCreatorTier(controller.GetCustomAttributes(inherit: true));

            var methods = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                if (IsIgnored(method))
                    continue;

                var httpAttributes = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToArray();
                if (httpAttributes.Length == 0)
                    continue;

                var methodAuthorize = method.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToArray();
                var allowAnonymous = classAllowAnonymous || method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
                var authorizeAttributes = classAuthorize.Concat(methodAuthorize).ToArray();
                var requiresAuth = !allowAnonymous && authorizeAttributes.Length > 0;
                var requiresCreatorTier = classRequiresCreatorTier || RequiresCreatorTier(method.GetCustomAttributes(inherit: true));

                var roles = authorizeAttributes
                    .SelectMany(attribute => SplitCsv(attribute.Roles))
                    .ToArray();
                var requiresRole = roles.Length > 0
                    ? NormalizeCsv(string.Join(",", roles))
                    : requiresAuth && requiresCreatorTier
                        ? "Creator"
                        : null;

                foreach (var httpAttribute in httpAttributes)
                {
                    var route = httpAttribute.Template ?? "";
                    var path = CombineRoutes(routePrefix, route);
                    foreach (var httpMethod in httpAttribute.HttpMethods)
                    {
                        yield return new RouteSecurity(
                            httpMethod.ToUpperInvariant(),
                            path,
                            requiresAuth,
                            requiresRole);
                    }
                }
            }
        }
    }

    private static bool IsIgnored(MemberInfo member) =>
        member.GetCustomAttributes<ApiExplorerSettingsAttribute>(inherit: true)
            .Any(attribute => attribute.IgnoreApi);

    private static bool RequiresCreatorTier(IEnumerable<object> attributes) =>
        attributes.Any(attribute =>
            string.Equals(attribute.GetType().Name, "RequireCreatorTierAttribute", StringComparison.Ordinal));

    private static string CombineRoutes(string routePrefix, string route)
    {
        var combined = route.StartsWith("/", StringComparison.Ordinal)
            ? route
            : string.Join("/", new[] { routePrefix, route }
                .Select(part => part.Trim('/'))
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        combined = "/" + combined.Trim('/');
        if (combined.Length > 1)
            combined = combined.TrimEnd('/');

        return RouteConstraintPattern().Replace(combined, "{$1}");
    }

    private static IEnumerable<string> SplitCsv(string? value) =>
        (value ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? NormalizeCsv(string? value)
    {
        var values = SplitCsv(value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return values.Length == 0 ? null : string.Join(",", values);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex(@"\{([^}:]+):[^}]+\}")]
    private static partial Regex RouteConstraintPattern();

    private sealed record RouteSecurity(
        string Method,
        string Path,
        bool RequiresAuth,
        string? RequiresRole);

    private sealed record EndpointManifest(
        string Version,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<EndpointManifestEntry> Endpoints);

    private sealed record EndpointManifestEntry(
        string Method,
        string Path,
        bool RequiresAuth,
        string Tag,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RequiresRole = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RequiresPolicy = null);
}
