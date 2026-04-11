using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cambrian.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cambrian.Api.Tests.Contract;

/// <summary>
/// Runtime smoke test for documented endpoints.
/// It exercises the live ASP.NET Core pipeline for every OpenAPI operation and
/// asserts the result is a controlled application/framework status, not a crash.
/// </summary>
public sealed class OpenApiEndpointCoverageTests : IClassFixture<CambrianApiFixture>
{
    private static readonly Regex PathParameterPattern = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    private static readonly HashSet<HttpStatusCode> AllowedStatuses =
    [
        HttpStatusCode.OK,
        HttpStatusCode.Created,
        HttpStatusCode.Accepted,
        HttpStatusCode.NoContent,
        HttpStatusCode.BadRequest,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
        HttpStatusCode.MethodNotAllowed,
        HttpStatusCode.Conflict,
        HttpStatusCode.UnsupportedMediaType,
        HttpStatusCode.PaymentRequired,
        HttpStatusCode.UnprocessableEntity,
    ];

    private readonly CambrianApiFixture _factory;

    public OpenApiEndpointCoverageTests(CambrianApiFixture factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApi_Operations_Return_Controlled_Status_Codes()
    {
        foreach (var operation in EnumerateOperations())
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            using var request = CreateRequest(operation.Method, operation.Path, operation.ContentType);
            using var response = await client.SendAsync(request);

            Assert.True(
                AllowedStatuses.Contains(response.StatusCode),
                $"Unexpected status {(int)response.StatusCode} ({response.StatusCode}) for {operation.Method} {operation.Path}");
        }
    }

    private static IEnumerable<(string Method, string Path, string? ContentType)> EnumerateOperations()
    {
        using var doc = LoadOpenApi();
        foreach (var pathProp in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var methodProp in pathProp.Value.EnumerateObject())
            {
                var contentType = TryGetRequestContentType(methodProp.Value);
                yield return (methodProp.Name.ToUpperInvariant(), pathProp.Name, contentType);
            }
        }
    }

    private static JsonDocument LoadOpenApi()
    {
        var basePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "openapi.v1.json");
        var json = File.ReadAllText(Path.GetFullPath(basePath));
        return JsonDocument.Parse(json);
    }

    private static string? TryGetRequestContentType(JsonElement operation)
    {
        if (!operation.TryGetProperty("requestBody", out var requestBody))
            return null;

        if (!requestBody.TryGetProperty("content", out var content))
            return null;

        foreach (var contentType in content.EnumerateObject())
        {
            return contentType.Name;
        }

        return null;
    }

    private static HttpRequestMessage CreateRequest(string method, string rawPath, string? contentType)
    {
        var path = PathParameterPattern.Replace(rawPath, match => ResolveRouteParameter(match.Groups[1].Value));
        var request = new HttpRequestMessage(new HttpMethod(method), path);

        if (method is "POST" or "PUT" or "PATCH")
        {
            if (string.Equals(contentType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new MultipartFormDataContent();
            }
            else
            {
                request.Content = new StringContent("{}", Encoding.UTF8, contentType ?? "application/json");
            }
        }

        return request;
    }

    private static string ResolveRouteParameter(string name)
    {
        var normalized = name.Trim('*').ToLowerInvariant();

        if (normalized.Contains("username") || normalized.Contains("slug"))
            return "test-user";

        if (normalized.Contains("email"))
            return "test@example.com";

        if (normalized.Contains("session"))
            return "cs_test_smoke";

        return "00000000-0000-0000-0000-000000000001";
    }
}
