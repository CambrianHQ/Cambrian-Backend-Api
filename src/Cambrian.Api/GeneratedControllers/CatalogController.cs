using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CatalogController : ControllerBase
{

        [HttpGet("catalog")]
        public IActionResult GET_catalog()
        {
            return Ok("stub");
        }
}
