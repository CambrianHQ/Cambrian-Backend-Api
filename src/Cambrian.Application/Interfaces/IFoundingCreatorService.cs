using Cambrian.Application.DTOs.FoundingCreator;

namespace Cambrian.Application.Interfaces;

public interface IFoundingCreatorService
{
    Task<FoundingCreatorDto?> EnrollFoundingCreatorAsync(string userId);
    Task<List<FoundingCreatorDto>> GetFoundingCreatorsAsync();
    Task<FoundingCreatorStatusDto?> GetFoundingStatusAsync(string userId);
}
