using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cambrian.Api.E2e;

/// <summary>
/// Minimal-API route group for the test-only E2E support surface. Mapped from Program.cs ONLY
/// when <see cref="E2eSupport.IsEnabled"/> is true, so the routes simply do not exist in
/// Production/Staging. Every request is authenticated with a constant-time-compared secret in
/// the <see cref="E2eSupport.SecretHeader"/> header. The group is excluded from the OpenAPI
/// contract and (living outside <c>Controllers/</c>) from the architecture validator.
/// </summary>
public static class E2eEndpoints
{
    public static RouteGroupBuilder MapE2e(this WebApplication app)
    {
        var group = app.MapGroup("/__e2e").ExcludeFromDescription();

        // Gate every request: re-check enablement (defense in depth) then the shared secret.
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var env = http.RequestServices.GetRequiredService<IHostEnvironment>();
            var cfg = http.RequestServices.GetRequiredService<IConfiguration>();

            if (!E2eSupport.IsEnabled(env, cfg))
                return Results.NotFound();

            var provided = http.Request.Headers[E2eSupport.SecretHeader].ToString();
            if (!E2eSupport.SecretMatches(provided, E2eSupport.ResolveSecret(cfg)))
                return Results.Json(new { success = false, error = "e2e_unauthorized" },
                    statusCode: StatusCodes.Status401Unauthorized);

            try
            {
                return await next(ctx);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { success = false, error = "e2e_bad_request", message = ex.Message },
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/reset", async (E2eScenarioService svc, bool? reseed, CancellationToken ct) =>
            Results.Json(await svc.ResetAsync(reseed ?? true, ct)));

        group.MapPost("/seed", async (E2eScenarioService svc, CancellationToken ct) =>
            Results.Json(await svc.SeedAsync(ct)));

        group.MapGet("/state", async (E2eScenarioService svc, string? email, Guid? trackId, CancellationToken ct) =>
            Results.Json(await svc.BuildStateAsync(null, email, trackId, ct)));

        group.MapGet("/state/{domain}", async (string domain, E2eScenarioService svc, string? email, Guid? trackId, CancellationToken ct) =>
            Results.Json(await svc.BuildStateAsync(domain, email, trackId, ct)));

        group.MapPost("/stripe/checkout-completed", async (E2eScenarioService svc, CheckoutCompletedRequest body, CancellationToken ct) =>
            Results.Json(await svc.SimulateCheckoutCompletedAsync(
                body.Email, body.Kind ?? "subscription", body.Tier, body.Credits, body.RecordId, body.EventId, body.SessionId, ct)));

        group.MapPost("/stripe/payment-failed", async (E2eScenarioService svc, CustomerEventRequest body, CancellationToken ct) =>
            Results.Json(await svc.SimulatePaymentFailedAsync(body.Email, body.EventId, ct)));

        group.MapPost("/stripe/subscription-cancelled", async (E2eScenarioService svc, CustomerEventRequest body, CancellationToken ct) =>
            Results.Json(await svc.SimulateSubscriptionCancelledAsync(body.Email, body.EventId, ct)));

        group.MapPost("/stripe/checkout-cancelled", async (E2eScenarioService svc, CustomerEventRequest body, CancellationToken ct) =>
            Results.Json(await svc.SimulateCheckoutCancelledAsync(body.Email, ct)));

        return group;
    }

    public sealed record CheckoutCompletedRequest(
        string Email, string? Kind, string? Tier, int? Credits, Guid? RecordId, string? EventId, string? SessionId);

    public sealed record CustomerEventRequest(string Email, string? EventId);
}
