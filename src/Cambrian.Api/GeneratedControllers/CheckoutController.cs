using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class CheckoutController : ControllerBase
{

        [HttpPost("checkout")]
        public IActionResult POST_checkout()
        {
            return Ok("stub");
        }
}
