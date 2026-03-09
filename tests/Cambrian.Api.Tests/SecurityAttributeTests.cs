using System.Reflection;
using Cambrian.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Tests;

/// <summary>
/// Static analysis tests that verify [Authorize] attributes are correctly
/// applied on controllers and actions that require authentication.
/// Ensures no accidental exposure of protected endpoints.
/// </summary>
public sealed class SecurityAttributeTests
{
    private static bool HasAuthorize(Type controller) =>
        controller.GetCustomAttribute<AuthorizeAttribute>() is not null;

    private static bool ActionHasAuthorize(MethodInfo method) =>
        method.GetCustomAttribute<AuthorizeAttribute>() is not null;

    private static bool ActionHasAllowAnonymous(MethodInfo method) =>
        method.GetCustomAttribute<AllowAnonymousAttribute>() is not null;

    private static IEnumerable<MethodInfo> GetActions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is HttpGetAttribute or HttpPostAttribute
                or HttpPutAttribute or HttpDeleteAttribute));

    // ── Controllers that must require auth at the class level ──

    [Theory]
    [InlineData(typeof(LibraryController))]
    [InlineData(typeof(PaymentsController))]
    [InlineData(typeof(WalletController))]
    [InlineData(typeof(SubscriptionsController))]
    [InlineData(typeof(BillingController))]
    [InlineData(typeof(DownloadController))]
    public void Controller_HasAuthorize(Type controllerType)
    {
        Assert.True(HasAuthorize(controllerType),
            $"{controllerType.Name} must have [Authorize] attribute");
    }

    // ── Specific endpoints that must allow anonymous ──

    [Fact]
    public void CatalogController_AllEndpoints_AreAnonymous()
    {
        Assert.False(HasAuthorize(typeof(CatalogController)));
    }

    [Fact]
    public void WebhookController_AllEndpoints_AreAnonymous()
    {
        Assert.False(HasAuthorize(typeof(WebhookController)));
    }

    [Fact]
    public void HealthController_AllEndpoints_AreAnonymous()
    {
        Assert.False(HasAuthorize(typeof(HealthController)));
    }

    // ── CheckoutController must require auth ──

    [Fact]
    public void CheckoutController_Checkout_RequiresAuth()
    {
        var action = typeof(CheckoutController)
            .GetMethod("Checkout", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(action);
        Assert.True(
            HasAuthorize(typeof(CheckoutController)) || ActionHasAuthorize(action!),
            "POST /checkout must require authentication");
    }

    // ── Subscription plans must be anonymous ──

    [Fact]
    public void SubscriptionsController_Plans_AllowsAnonymous()
    {
        var method = typeof(SubscriptionsController)
            .GetMethod("Plans", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(ActionHasAllowAnonymous(method!),
            "GET /subscriptions/plans must be accessible without auth");
    }

    // ── Payments result must be anonymous (for Stripe redirect) ──

    [Fact]
    public void PaymentsController_Result_AllowsAnonymous()
    {
        var method = typeof(PaymentsController)
            .GetMethod("Result", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(ActionHasAllowAnonymous(method!),
            "GET /payments/result must be accessible without auth for Stripe redirect");
    }

    // ── Auth endpoints: register and login must NOT require auth ──

    [Fact]
    public void AuthController_RegisterAndLogin_DoNotRequireAuth()
    {
        var registerMethod = typeof(AuthController)
            .GetMethod("Register", BindingFlags.Public | BindingFlags.Instance);
        var loginMethod = typeof(AuthController)
            .GetMethod("Login", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(registerMethod);
        Assert.NotNull(loginMethod);

        Assert.False(ActionHasAuthorize(registerMethod!),
            "POST /auth/register must not require auth");
        Assert.False(ActionHasAuthorize(loginMethod!),
            "POST /auth/login must not require auth");
    }

    // ── Auth /me must require auth ──

    [Fact]
    public void AuthController_Me_RequiresAuth()
    {
        var method = typeof(AuthController)
            .GetMethod("Me", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(ActionHasAuthorize(method!),
            "GET /auth/me must require authentication");
    }

    // ── Auth logout must require auth ──

    [Fact]
    public void AuthController_Logout_RequiresAuth()
    {
        var method = typeof(AuthController)
            .GetMethod("Logout", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.True(ActionHasAuthorize(method!),
            "POST /auth/logout must require authentication");
    }

    // ── CatalogController's upload requires Creator role ──

    [Fact]
    public void CatalogController_TracksUpload_RequiresCreatorRole()
    {
        var method = typeof(CatalogController)
            .GetMethod("TracksUpload", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attr);
        Assert.Contains("Creator", attr!.Roles ?? "");
    }

    // ── Stream start/stop require auth, but list/stream are anonymous ──

    [Fact]
    public void StreamController_StartAndStop_RequireAuth()
    {
        var start = typeof(StreamController)
            .GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
        var stop = typeof(StreamController)
            .GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(start);
        Assert.NotNull(stop);
        Assert.True(ActionHasAuthorize(start!), "POST /stream/start must require auth");
        Assert.True(ActionHasAuthorize(stop!), "POST /stream/stop must require auth");
    }
}
