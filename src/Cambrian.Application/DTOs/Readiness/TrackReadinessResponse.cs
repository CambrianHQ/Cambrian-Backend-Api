namespace Cambrian.Application.DTOs.Readiness;

/// <summary>Weighted release-readiness score for a track (contract: GET /api/tracks/{id}/readiness).</summary>
public sealed class TrackReadinessResponse
{
    public int Score { get; init; }
    public IReadOnlyList<ReadinessCheck> Checks { get; init; } = Array.Empty<ReadinessCheck>();
}

public sealed class ReadinessCheck
{
    /// <summary>loudness | metadata | aiDisclosure | cover | provenance.</summary>
    public string Key { get; init; } = "";

    /// <summary>pass | warn | fail.</summary>
    public string Status { get; init; } = "fail";

    public string Detail { get; init; } = "";
}
