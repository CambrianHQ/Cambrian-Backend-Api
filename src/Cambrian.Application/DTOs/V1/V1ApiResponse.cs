namespace Cambrian.Application.DTOs.V1;

/// <summary>
/// Canonical envelope for all /api/v1/* responses.
/// Always serializes the same shape regardless of success/failure so clients
/// can parse with one branch:
///   { "success": bool, "data": T?, "error": string?, "meta": {...}? }
/// </summary>
public sealed record V1ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }
    public V1PaginationMeta? Meta { get; init; }

    public static V1ApiResponse<T> Ok(T data, V1PaginationMeta? meta = null) =>
        new() { Success = true, Data = data, Meta = meta };

    public static V1ApiResponse<T> Fail(string error) =>
        new() { Success = false, Error = error };
}

/// <summary>
/// Pagination metadata returned alongside list responses. Name matches the
/// existing <c>V1PaginationMeta</c> schema in <c>contracts/openapi.v1.json</c>
/// so the envelope doesn't duplicate types in the public contract.
/// </summary>
public sealed record V1PaginationMeta
{
    public int Page { get; init; }
    public int Limit { get; init; }
    public int Total { get; init; }
    public int TotalPages { get; init; }
    public bool HasNext { get; init; }
    public bool HasPrev { get; init; }
}
