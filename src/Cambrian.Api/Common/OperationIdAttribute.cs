namespace Cambrian.Api.Common;

/// <summary>
/// Maps a controller action to its OpenAPI operationId for 1:1 contract binding.
/// Used by contract enforcement tests to verify exact mapping between
/// the OpenAPI spec and controller implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class OperationIdAttribute : Attribute
{
    public string OperationId { get; }

    public OperationIdAttribute(string operationId) => OperationId = operationId;
}
