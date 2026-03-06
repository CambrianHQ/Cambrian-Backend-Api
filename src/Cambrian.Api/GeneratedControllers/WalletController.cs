using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class WalletController : ControllerBase
{

        [HttpGet("wallet")]
        public IActionResult GET_wallet()
        {
            return Ok("stub");
        }

        [HttpPost("wallet/withdraw")]
        public IActionResult POST_wallet_withdraw()
        {
            return Ok("stub");
        }

        [HttpGet("wallet/history")]
        public IActionResult GET_wallet_history()
        {
            return Ok("stub");
        }
}
