namespace Cambrian.Api.Common;

public static class StripeConnectAvailability
{
    public const string FeatureFlagName = "StripeConnectEnabled";
    public const string ErrorCode = "STRIPE_NOT_READY";
    public const string ErrorMessage = "Payouts not enabled yet";

    public static object BuildBlockedResponse() => new
    {
        code = ErrorCode,
        message = ErrorMessage
    };
}
