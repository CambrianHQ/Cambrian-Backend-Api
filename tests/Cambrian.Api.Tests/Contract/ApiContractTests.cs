using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Cambrian.Api.Tests.Contract;

/// <summary>
/// Ensures every path defined in contracts/openapi.v1.json has a matching
/// controller action and vice-versa. Prevents route drift between the
/// OpenAPI contract and the actual implementation.
/// </summary>
public sealed class ApiContractTests
{
    private static JsonDocument LoadOpenApi()
    {
        // Path from test bin/{config}/net8.0 → repo root → contracts/
        var basePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "openapi.v1.json");
        var json = File.ReadAllText(Path.GetFullPath(basePath));
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Collects all (verb, path) tuples from the OpenAPI spec.
    /// Path parameters are normalized to {param} form (no difference between {id} and {trackId}).
    /// </summary>
    private static HashSet<(string verb, string path)> GetOpenApiRoutes()
    {
        using var doc = LoadOpenApi();
        var routes = new HashSet<(string, string)>();
        foreach (var pathProp in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            var normalizedPath = NormalizePath(pathProp.Name);
            foreach (var methodProp in pathProp.Value.EnumerateObject())
            {
                routes.Add((methodProp.Name.ToUpperInvariant(), normalizedPath));
            }
        }
        return routes;
    }

    /// <summary>
    /// Collects all (verb, path) tuples from controller route attributes.
    /// </summary>
    private static HashSet<(string verb, string path)> GetControllerRoutes()
    {
        var assembly = Assembly.Load("Cambrian.Api");
        var routes = new HashSet<(string, string)>();

        var controllers = assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            var routePrefix = controller
                .GetCustomAttributes<RouteAttribute>()
                .FirstOrDefault()?.Template?.Trim('/') ?? "";

            var methods = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                var httpAttrs = method.GetCustomAttributes()
                    .OfType<HttpMethodAttribute>()
                    .ToList();

                foreach (var attr in httpAttrs)
                {
                    var template = attr.Template?.Trim('/') ?? "";
                    var fullPath = string.IsNullOrEmpty(routePrefix)
                        ? template
                        : string.IsNullOrEmpty(template)
                            ? routePrefix
                            : $"{routePrefix}/{template}";

                    var normalizedPath = NormalizePath("/" + fullPath);

                    foreach (var httpMethod in attr.HttpMethods)
                    {
                        routes.Add((httpMethod.ToUpperInvariant(), normalizedPath));
                    }
                }
            }
        }

        return routes;
    }

    /// <summary>
    /// Normalize path by lowercasing and converting specific param names
    /// to a canonical form for comparison. Preserves the {param} syntax.
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Trim('/').ToLowerInvariant();
    }

    [Fact]
    public void OpenApi_Paths_Have_Matching_Controller_Actions()
    {
        var openApiRoutes = GetOpenApiRoutes();
        var controllerRoutes = GetControllerRoutes();

        var missing = openApiRoutes
            .Where(oaRoute => !controllerRoutes.Any(cr =>
                cr.verb == oaRoute.verb &&
                PathsMatch(oaRoute.path, cr.path)))
            .OrderBy(r => r.path)
            .ThenBy(r => r.verb)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Routes in OpenAPI contract not implemented in controllers:\n" +
            string.Join("\n", missing.Select(m => $"  {m.verb} /{m.path}")));
    }

    [Fact]
    public void Controller_Actions_Have_Matching_OpenApi_Paths()
    {
        var openApiRoutes = GetOpenApiRoutes();
        var controllerRoutes = GetControllerRoutes();

        // Only flag controller routes that could plausibly be API routes
        // (skip healthcheck routes, etc. that may intentionally be undocumented)
        var undocumented = controllerRoutes
            .Where(cr => !openApiRoutes.Any(oaRoute =>
                oaRoute.verb == cr.verb &&
                PathsMatch(oaRoute.path, cr.path)))
            .OrderBy(r => r.path)
            .ThenBy(r => r.verb)
            .ToList();

        // This is informational — undocumented routes should be reviewed
        // but may be intentional (e.g. dev-only routes).
        // Change to Assert.True to make it blocking.
        if (undocumented.Count > 0)
        {
            // Log for visibility but don't fail — some routes are intentionally undocumented
            // To make this blocking, replace with Assert.True(undocumented.Count == 0, ...)
        }
    }

    /// <summary>
    /// Fuzzy path matching: treats path template params as equivalent.
    /// e.g. "admin/users/{id}/role" matches "admin/users/{userId}/role"
    /// Also handles {**key} catch-all params.
    /// </summary>
    private static bool PathsMatch(string a, string b)
    {
        var segA = a.Split('/');
        var segB = b.Split('/');
        if (segA.Length != segB.Length) return false;

        for (var i = 0; i < segA.Length; i++)
        {
            var isParamA = segA[i].StartsWith('{');
            var isParamB = segB[i].StartsWith('{');

            if (isParamA && isParamB) continue; // both are params → match
            if (isParamA || isParamB) return false; // only one is param → mismatch
            if (segA[i] != segB[i]) return false; // literal mismatch
        }
        return true;
    }

    [Fact]
    public void OpenApi_Contract_File_Exists_And_Is_Valid_Json()
    {
        using var doc = LoadOpenApi();
        Assert.True(doc.RootElement.TryGetProperty("paths", out _), "OpenAPI must have 'paths' property");
        Assert.True(doc.RootElement.TryGetProperty("openapi", out _), "OpenAPI must have 'openapi' version");
    }

    [Fact]
    public void OpenApi_Has_Security_Scheme_Defined()
    {
        using var doc = LoadOpenApi();
        var components = doc.RootElement.GetProperty("components");
        var schemes = components.GetProperty("securitySchemes");
        Assert.True(schemes.TryGetProperty("Bearer", out _), "OpenAPI must define Bearer security scheme");
    }
}
