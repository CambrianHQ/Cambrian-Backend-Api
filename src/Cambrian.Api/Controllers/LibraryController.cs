using Cambrian.Application.DTOs.Library;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("library")]
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
        var result = await _library.GetLibraryAsync();
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Save(LibrarySaveRequest req)
    {
        await _library.SaveAsync(req);
        return Ok();
    }

    [HttpDelete("{trackId}")]
    public async Task<IActionResult> Remove(string trackId)
    {
        await _library.RemoveAsync(trackId);
        return Ok();
    }
}
