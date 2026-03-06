using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class PurchasesController : ControllerBase
{

        [HttpPost("purchases")]
        public IActionResult POST_purchases()
        {
            return Ok("stub");
        }

        [HttpPost("purchases/credit-creator")]
        public IActionResult POST_purchases_credit_creator()
        {
            return Ok("stub");
        }
}
