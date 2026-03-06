using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("library")]
[Authorize]
public class LibraryController : ControllerBase
{
    private readonly ILibraryService _library;

    public LibraryController(ILibraryService library)
    {
        _library = library;
    }

    [HttpGet]
    public async Task<IActionResult> GetLibrary()
    {
        var result = await _library.GetLibraryAsync(User);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Save(LibrarySaveRequest request)
    {
        await _library.SaveAsync(User, request);
        return Ok();
    }

    [HttpDelete("{trackId}")]
    public async Task<IActionResult> Remove(string trackId)
    {
        await _library.RemoveAsync(User, trackId);
        return Ok();
    }

    [HttpPost("{trackId}")]
    public async Task<IActionResult> AddById(string trackId)
    {
        await _library.SaveAsync(User, new LibrarySaveRequest { TrackId = trackId });
        return Ok();
    }

    [HttpGet("purchased-track-ids")]
    public async Task<IActionResult> PurchasedIds()
    {
        var ids = await _library.GetPurchasedTrackIdsAsync(User);
        return Ok(ids);
    }
}