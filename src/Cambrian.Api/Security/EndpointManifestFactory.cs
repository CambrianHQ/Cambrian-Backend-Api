using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace Cambrian.Api.Security;

internal static partial class EndpointManifestFactory
{
    public static EndpointManifest Build(IEnumerable<EndpointDataSource> endpointDataSources, DateTimeOffset generatedAt)
    {
        var endpoints = endpointDataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(ToManifestEntries)
            .GroupBy(endpoint => $"{endpoint.Method} {endpoint.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeDuplicates(group))
            .OrderBy(endpoint => endpoint.Path, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Method, StringComparer.Ordinal)
            .ToArray();

        return new EndpointManifest("v1", generatedAt, endpoints);
    }

    private static IEnumerable<EndpointManifestEntry> ToManifestEntries(RouteEndpoint endpoint)
    {
        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        var ignoredByApiExplorer = endpoint.Metadata
            .GetOrderedMetadata<ApiExplorerSettingsAttribute>()
            .Any(attribute => attribute.IgnoreApi);
        if (action is null || ignoredByApiExplorer)
            yield break;

        var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        if (httpMethods is null || httpMethods.Count == 0)
            yield break;

        var path = NormalizePath(endpoint.RoutePattern.RawText ?? string.Empty);
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        var allowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        var authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var requiresAuth = !allowAnonymous && authorizeData.Count > 0;
        var roles = requiresAuth ? JoinDistinct(authorizeData.SelectMany(GetRoles)) : null;
        var policies = requiresAuth ? JoinDistinct(authorizeData.Select(data => data.Policy)) : null;

        if (requiresAuth && string.IsNullOrWhiteSpace(roles) && RequiresCreatorTier(endpoint))
            roles = "Creator";

        foreach (var method in httpMethods)
        {
            yield return new EndpointManifestEntry(
                method.ToUpperInvariant(),
                path,
                requiresAuth,
                action.ControllerName,
                string.IsNullOrWhiteSpace(roles) ? null : roles,
                string.IsNullOrWhiteSpace(policies) ? null : policies);
        }
    }

    private static EndpointManifestEntry MergeDuplicates(IGrouping<string, EndpointManifestEntry> group)
    {
        var first = group.First();
        var requiresAuth = group.Any(endpoint => endpoint.RequiresAuth);
        var roles = JoinDistinct(group.Select(endpoint => endpoint.RequiresRole));
        var policies = JoinDistinct(group.Select(endpoint => endpoint.RequiresPolicy));

        return first with
        {
            RequiresAuth = requiresAuth,
            RequiresRole = string.IsNullOrWhiteSpace(roles) ? null : roles,
            RequiresPolicy = string.IsNullOrWhiteSpace(policies) ? null : policies
        };
    }

    private static bool RequiresCreatorTier(RouteEndpoint endpoint) =>
        endpoint.Metadata.Any(metadata =>
            string.Equals(metadata.GetType().Name, "RequireCreatorTierAttribute", StringComparison.Ordinal));

    private static IEnumerable<string> GetRoles(IAuthorizeData authorizeData) =>
        (authorizeData.Roles ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string? JoinDistinct(IEnumerable<string?> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return distinct.Length == 0 ? null : string.Join(",", distinct);
    }

    private static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        var path = rawPath.StartsWith("/", StringComparison.Ordinal)
            ? rawPath
            : "/" + rawPath.TrimStart('/');
        if (path.Length > 1)
            path = path.TrimEnd('/');

        return RouteConstraintPattern().Replace(path, "{$1}");
    }

    [GeneratedRegex(@"\{([^}:]+):[^}]+\}")]
    private static partial Regex RouteConstraintPattern();

    public sealed record EndpointManifest(
        string Version,
        DateTimeOffset GeneratedAt,
        IReadOnlyList<EndpointManifestEntry> Endpoints);

    public sealed record EndpointManifestEntry(
        string Method,
        string Path,
        bool RequiresAuth,
        string Tag,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RequiresRole = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RequiresPolicy = null);
}
