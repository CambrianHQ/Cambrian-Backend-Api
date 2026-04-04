using Cambrian.Application.AI.Discovery.Queries;
using Cambrian.Domain.Entities;

namespace Cambrian.Application.AI.Discovery.Ranking;

public interface ITrackRankingService
{
    /// <summary>
    /// Compute a relevance score (0–1) for a track given the search query.
    /// </summary>
    double ComputeScore(Track track, SearchTracksQuery query);
}
