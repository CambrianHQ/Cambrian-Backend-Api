using Microsoft.AspNetCore.Http;

namespace Cambrian.Application.DTOs.Catalog;

public class UpdateTrackCoverArtRequest
{
    public IFormFile? CoverArt { get; set; }
}
