namespace Cambrian.Application.AI.Discovery.Dtos;

public class AiCreatorSummaryDto
{
    public string CreatorId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool? VerifiedCreator { get; set; }
}
