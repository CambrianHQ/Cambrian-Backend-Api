using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("earnings")]
[Authorize]
public class EarningsController : BaseController
{
    [HttpGet]
    public IActionResult Get()
    {
        return OkResponse(new { total = 0m, pending = 0m, available = 0m });
    }
}
