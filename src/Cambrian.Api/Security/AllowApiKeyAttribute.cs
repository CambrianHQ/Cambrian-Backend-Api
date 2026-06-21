namespace Cambrian.Api.Security;

/// <summary>
/// Explicitly opts an endpoint into X-API-Key authentication.
/// API keys are rejected everywhere else, including endpoints with generic
/// <c>[Authorize]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowApiKeyAttribute : Attribute;
