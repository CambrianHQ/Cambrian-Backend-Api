namespace Cambrian.Application.Interfaces;

public interface IDownloadService
{
    Task<object> GetDownloadUrlAsync(Guid trackId, string userId);

    Task<object> GetSignedUrlAsync(Guid trackId, string userId);
}
