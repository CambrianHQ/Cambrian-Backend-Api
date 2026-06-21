namespace Cambrian.Api.Common;

public static class CheckoutAvailability
{
    public const string ErrorCode = "checkout_disabled";
    public const string ErrorMessage = "Checkout is temporarily unavailable. Please try again shortly.";

    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("Checkout:Enabled")
        ?? configuration.GetValue<bool?>("CHECKOUT_ENABLED")
        ?? true;

    public static object BuildBlockedResponse() => new
    {
        success = false,
        data = (object?)null,
        message = ErrorMessage,
        error = ErrorCode,
    };
}
