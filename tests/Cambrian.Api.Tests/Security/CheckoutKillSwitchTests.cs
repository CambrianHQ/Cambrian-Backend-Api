using System.Reflection;
using Cambrian.Api.Controllers;
using Cambrian.Api.Middleware;

namespace Cambrian.Api.Tests.Security;

public sealed class CheckoutKillSwitchTests
{
    public static TheoryData<Type, string> ChargeCreatingActions => new()
    {
        { typeof(BillingController), nameof(BillingController.Checkout) },
        { typeof(BillingController), nameof(BillingController.CheckoutSession) },
        { typeof(BillingController), nameof(BillingController.ApiCheckout) },
        { typeof(ArtistsController), nameof(ArtistsController.Tip) },
        { typeof(ArtistsController), nameof(ArtistsController.Subscribe) },
        { typeof(AuthorshipRecordsController), nameof(AuthorshipRecordsController.Create) },
        { typeof(ReleaseReadyController), nameof(ReleaseReadyController.BuyCredits) },
    };

    [Theory]
    [MemberData(nameof(ChargeCreatingActions))]
    public void EveryChargeCreatingAction_HasOperationalKillSwitch(
        Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<RequireCheckoutEnabledAttribute>());
    }
}
