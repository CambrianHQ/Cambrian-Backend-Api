namespace Cambrian.Api.Security;

public static class AuthenticationConstants
{
    public const string AuthMethodClaim = "auth_method";
    public const string AuthMethodApiKey = "api_key";
    public const string AuthTransportClaim = "auth_transport";
    public const string AuthTransportBearer = "bearer";
    public const string AuthTransportCookie = "cookie";

    public const string InteractiveUserPolicy = "InteractiveUser";
    public const string ApiKeyIntegrationPolicy = "ApiKeyIntegration";
}
