using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class PayoutsController : ControllerBase
{

        [HttpPost("payouts/connect-stripe")]
        public IActionResult POST_payouts_connect_stripe()
        {
            return Ok("stub");
        }

        [HttpGet("payouts/connect-status")]
        public IActionResult GET_payouts_connect_status()
        {
            return Ok("stub");
        }

        [HttpGet("payouts/stripe-dashboard")]
        public IActionResult GET_payouts_stripe_dashboard()
        {
            return Ok("stub");
        }

        [HttpGet("payouts/account")]
        public IActionResult GET_payouts_account()
        {
            return Ok("stub");
        }

        [HttpPost("payouts/connect")]
        public IActionResult POST_payouts_connect()
        {
            return Ok("stub");
        }

        [HttpDelete("payouts/disconnect")]
        public IActionResult DELETE_payouts_disconnect()
        {
            return Ok("stub");
        }

        [HttpPost("payouts/disconnect")]
        public IActionResult POST_payouts_disconnect()
        {
            return Ok("stub");
        }

        [HttpGet("payouts/earnings")]
        public IActionResult GET_payouts_earnings()
        {
            return Ok("stub");
        }

        [HttpPost("payouts/request")]
        public IActionResult POST_payouts_request()
        {
            return Ok("stub");
        }

        [HttpGet("payouts/history")]
        public IActionResult GET_payouts_history()
        {
            return Ok("stub");
        }

        [HttpPost("payouts/settings")]
        public IActionResult POST_payouts_settings()
        {
            return Ok("stub");
        }

        [HttpPut("payouts/settings")]
        public IActionResult PUT_payouts_settings()
        {
            return Ok("stub");
        }
}
