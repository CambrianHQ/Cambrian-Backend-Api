using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class PaymentsController : ControllerBase
{

        [HttpPost("payments/checkout")]
        public IActionResult POST_payments_checkout()
        {
            return Ok("stub");
        }

        [HttpGet("payments/state")]
        public IActionResult GET_payments_state()
        {
            return Ok("stub");
        }

        [HttpGet("payments/result")]
        public IActionResult GET_payments_result()
        {
            return Ok("stub");
        }

        [HttpPost("payments/process")]
        public IActionResult POST_payments_process()
        {
            return Ok("stub");
        }
}
