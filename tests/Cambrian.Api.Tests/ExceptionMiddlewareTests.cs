using System.Net;
using System.Text.Json;
using Cambrian.Api.Common;
using Cambrian.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Tests for the global ExceptionMiddleware that maps domain exceptions
/// to the correct HTTP status codes and ApiResponse envelopes.
/// </summary>
public sealed class ExceptionMiddlewareTests
{
    private static ExceptionMiddleware CreateMiddleware(
        RequestDelegate next,
        bool isProduction = false)
    {
        var logger = Substitute.For<ILogger<ExceptionMiddleware>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isProduction ? "Production" : "Development");
        return new ExceptionMiddleware(next, logger, env);
    }

    private static async Task<(int StatusCode, ApiResponse<object?> Body)> InvokeAndRead(
        RequestDelegate next,
        bool isProduction = false)
    {
        var middleware = CreateMiddleware(next, isProduction);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var envelope = JsonSerializer.Deserialize<ApiResponse<object?>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        return (context.Response.StatusCode, envelope);
    }

    [Fact]
    public async Task UnauthorizedAccessException_Returns403()
    {
        var (status, body) = await InvokeAndRead(
            _ => throw new UnauthorizedAccessException("Not allowed"));

        Assert.Equal((int)HttpStatusCode.Forbidden, status);
        Assert.False(body.Success);
        Assert.Contains("Not allowed", body.Error);
    }

    [Fact]
    public async Task KeyNotFoundException_Returns404()
    {
        var (status, body) = await InvokeAndRead(
            _ => throw new KeyNotFoundException("Track not found"));

        Assert.Equal((int)HttpStatusCode.NotFound, status);
        Assert.False(body.Success);
        Assert.Contains("Track not found", body.Error);
    }

    [Fact]
    public async Task ArgumentException_Returns400()
    {
        var (status, body) = await InvokeAndRead(
            _ => throw new ArgumentException("Invalid trackId"));

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.False(body.Success);
        Assert.Contains("Invalid trackId", body.Error);
    }

    [Fact]
    public async Task InvalidOperationException_Returns400()
    {
        var (status, body) = await InvokeAndRead(
            _ => throw new InvalidOperationException("Already purchased"));

        Assert.Equal((int)HttpStatusCode.BadRequest, status);
        Assert.False(body.Success);
        Assert.Contains("Already purchased", body.Error);
    }

    [Fact]
    public async Task GenericException_Returns500_InDevelopment_WithMessage()
    {
        var (status, body) = await InvokeAndRead(
            _ => throw new NullReferenceException("something broke"),
            isProduction: false);

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.False(body.Success);
        Assert.Contains("something broke", body.Error);
    }

    [Fact]
    public async Task GenericException_Returns500_InProduction_WithGenericMessage()
    {
        var (status, body) = await InvokeAndRead(
            _ => throw new NullReferenceException("internal detail"),
            isProduction: true);

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.False(body.Success);
        Assert.Equal("An unexpected error occurred.", body.Error);
    }

    [Fact]
    public async Task NoException_PassesThrough_WithoutChangingResponse()
    {
        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
    }
}
