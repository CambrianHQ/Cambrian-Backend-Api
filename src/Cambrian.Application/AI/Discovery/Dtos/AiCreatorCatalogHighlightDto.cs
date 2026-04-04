namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiCreatorCatalogHighlightDto
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BestUseCase { get; set; } = string.Empty;
    public bool PreviewAvailable { get; set; }
}
