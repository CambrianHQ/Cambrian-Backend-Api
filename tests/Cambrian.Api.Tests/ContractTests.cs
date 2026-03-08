using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Cambrian.Api.Tests;

public sealed class ContractTests
{
    private static readonly string ContractPath = Path.Combine(
        FindRepoRoot(), "contracts", "openapi.v1.json");

    [Fact]
    public void AllRoutesMustExistInOpenApi()
    {
        var openApiRoutes = LoadOpenApiRoutes();
        var controllerRoutes = GetControllerRoutes();

        var missing = controllerRoutes
            .Where(r => !openApiRoutes.Contains(r))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Routes found in controllers but missing from OpenAPI contract:\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void OpenApiContractFileExists()
    {
        Assert.True(File.Exists(ContractPath),
            $"OpenAPI contract not found at {ContractPath}");
    }

    private static HashSet<string> LoadOpenApiRoutes()
    {
        var json = File.ReadAllText(ContractPath);
        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths.EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                routes.Add($"{method.Name.ToUpperInvariant()} {path.Name}");
            }
        }

        return routes;
    }

    private static List<string> GetControllerRoutes()
    {
        var controllerAssembly = typeof(Controllers.AuthController).Assembly;

        var routes = new List<string>();

        var controllers = controllerAssembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            var routeAttr = controller.GetCustomAttribute<RouteAttribute>();
            var prefix = routeAttr?.Template ?? string.Empty;

            var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                var httpAttrs = method.GetCustomAttributes()
                    .OfType<HttpMethodAttribute>()
                    .ToList();

                foreach (var attr in httpAttrs)
                {
                    var template = attr.Template;

                    // In ASP.NET Core, a template starting with "/" is absolute
                    // and overrides the controller-level [Route] prefix.
                    string path;
                    if (!string.IsNullOrEmpty(template) && template.StartsWith("/"))
                        path = template;
                    else if (string.IsNullOrEmpty(template))
                        path = $"/{prefix}";
                    else
                        path = $"/{prefix}/{template}";

                    path = path.Replace("//", "/");

                    foreach (var httpMethod in attr.HttpMethods)
                    {
                        routes.Add($"{httpMethod.ToUpperInvariant()} {path}");
                    }
                }
            }
        }

        return routes;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Cambrian.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Fallback: walk up from the test assembly location
        dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        for (var i = 0; i < 6 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Cambrian.sln")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not find repository root (Cambrian.sln)");
    }
}
