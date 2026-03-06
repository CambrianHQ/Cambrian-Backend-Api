using System.Text.Json;
using System.Text.RegularExpressions;

public static class OpenApiControllerGenerator
{
    public static void Run()
    {
        Console.WriteLine("Generating controllers from OpenAPI...");

        // Resolve contracts dir relative to project (../../contracts from src/Cambrian.Api)
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", ".."));
        var contractsPath = Path.Combine(repoRoot, "contracts", "openapi.v1.json");
        if (!File.Exists(contractsPath))
        {
            // Fallback: walk up from cwd
            var dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "contracts", "openapi.v1.json")))
                dir = Path.GetDirectoryName(dir);
            contractsPath = dir != null ? Path.Combine(dir, "contracts", "openapi.v1.json") : contractsPath;
        }

        Console.WriteLine($"Reading spec from: {contractsPath}");
        var json = File.ReadAllText(contractsPath);
        var outputDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(contractsPath)!, "..", "src", "Cambrian.Api", "GeneratedControllers"));

        Directory.CreateDirectory(outputDir);
        Console.WriteLine($"Output dir: {outputDir}");

        using var doc = JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        foreach (var path in paths.EnumerateObject())
        {
            var route = path.Name;

            foreach (var method in path.Value.EnumerateObject())
            {
                var httpMethod = method.Name.ToUpper();

                var controllerName = GuessController(route);

                var file = $"{outputDir}/{controllerName}.cs";

                var actionName = GenerateActionName(httpMethod, route);

                var methodAttribute = $"[Http{httpMethod[0]}{httpMethod.Substring(1).ToLower()}(\"{route.TrimStart('/')}\")]";

                var action = $@"
        {methodAttribute}
        public IActionResult {actionName}()
        {{
            return Ok(""stub"");
        }}
";

                if (!File.Exists(file))
                {
                    File.WriteAllText(file, CreateControllerTemplate(controllerName));
                }

                File.AppendAllText(file, action);
            }
        }

        // Seal each generated file with closing brace
        foreach (var file in Directory.GetFiles(outputDir, "*.cs"))
        {
            File.AppendAllText(file, "}\n");
        }

        Console.WriteLine("Controller generation complete.");
    }

    static string GuessController(string route)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return "RootController";

        var name = Regex.Replace(parts[0], @"\{.*\}", "");

        return $"{Cap(name)}Controller";
    }

    static string GenerateActionName(string method, string route)
    {
        var clean = route
            .Replace("/", "_")
            .Replace("{", "")
            .Replace("}", "")
            .Replace("-", "_");

        return $"{method}_{clean}".Replace("__", "_").TrimEnd('_');
    }

    static string CreateControllerTemplate(string name)
    {
        return $@"using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("""")]
public class {name} : ControllerBase
{{
";
    }

    static string Cap(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToUpper(s[0]) + s.Substring(1);
    }
}
