namespace Cambrian.Application.DTOs.Catalog;

public sealed class BatchUploadRequest
{
    public List<UploadTrackRequest> Tracks { get; set; } = new();
}

public sealed class BatchUploadTrackResult
{
    public int Index { get; init; }
    public string? FileName { get; init; }
    public bool Success { get; init; }
    public UploadTrackResponse? Track { get; init; }
    public BatchUploadError? Error { get; init; }

    public static BatchUploadTrackResult Succeeded(int index, string? fileName, UploadTrackResponse track) =>
        new() { Index = index, FileName = fileName, Success = true, Track = track };

    public static BatchUploadTrackResult Failed(int index, string? fileName, string code, string message, string category) =>
        new() { Index = index, FileName = fileName, Success = false, Error = new BatchUploadError(code, message, category) };
}

public sealed record BatchUploadError(string Code, string Message, string Category);
