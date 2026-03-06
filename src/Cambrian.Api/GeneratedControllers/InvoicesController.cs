using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class InvoicesController : ControllerBase
{

        [HttpGet("invoices")]
        public IActionResult GET_invoices()
        {
            return Ok("stub");
        }

        [HttpGet("invoices/{invoiceId}")]
        public IActionResult GET_invoices_invoiceId()
        {
            return Ok("stub");
        }

        [HttpGet("invoices/{invoiceId}/download")]
        public IActionResult GET_invoices_invoiceId_download()
        {
            return Ok("stub");
        }
}
