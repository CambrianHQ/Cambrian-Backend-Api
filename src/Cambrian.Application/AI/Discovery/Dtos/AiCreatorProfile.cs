namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiCreatorProfile
{
    public string CreatorId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public string? Bio { get; set; }

    public bool VerifiedCreator { get; set; }

    public string? AvatarUrl { get; set; }
    public string? ProfileUrl { get; set; }

    public int TrackCount { get; set; }

    public List<string> FeaturedGenres { get; set; } = new();
    public List<string> FeaturedMoods { get; set; } = new();

    public List<AiCreatorCatalogHighlight> CatalogHighlights { get; set; } = new();
}
