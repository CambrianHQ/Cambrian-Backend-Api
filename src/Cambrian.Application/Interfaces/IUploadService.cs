using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Application.Interfaces;

public interface IUploadService
{
    Task<string> Upload(UploadTrackRequest request);
}