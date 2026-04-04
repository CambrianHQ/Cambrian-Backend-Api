namespace Cambrian.Application.AI.Discovery.Queries;

public class GetTrackDetailsQuery
{
    /// <summary>CambrianTrackId (e.g. "CAMB-TRK-A1B2C3D4") or Guid.</summary>
    public string TrackId { get; set; } = string.Empty;
}
