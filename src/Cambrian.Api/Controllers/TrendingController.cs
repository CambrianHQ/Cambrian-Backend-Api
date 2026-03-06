using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("trending")]
public class TrendingController : BaseController
{
    [HttpGet]
    public IActionResult Get([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        return OkResponse(Array.Empty<object>());
    }
}
