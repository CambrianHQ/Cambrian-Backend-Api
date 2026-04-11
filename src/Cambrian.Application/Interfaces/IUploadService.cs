using Cambrian.Application.DTOs.Catalog;
using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.Interfaces;

public interface IUploadService
{
    Task<UploadTrackResponse> Upload(UploadTrackRequest request);

    Task<string> UploadCoverArtAsync(string creatorId, IFormFile coverArt);
}
