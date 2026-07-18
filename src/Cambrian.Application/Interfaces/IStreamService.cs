namespace Cambrian.Application.Interfaces;

public interface IStreamService
{
    Task<IReadOnlyCollection<object>> ListStreamableAsync(int take = 20);

    Task<object> GetStreamUrlAsync(string trackId);
}
