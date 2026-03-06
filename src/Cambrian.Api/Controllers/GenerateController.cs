using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("generate")]
[Authorize]
public class GenerateController : BaseController
{
    [HttpPost]
    public IActionResult Generate()
    {
        return OkResponse(new { message = "stub" });
    }
}
