using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("invoices")]
[Authorize]
public class InvoiceController : BaseController
{
    [HttpGet]
    public IActionResult List()
    {
        return OkResponse(Array.Empty<object>());
    }

    [HttpGet("{invoiceId}")]
    public IActionResult Get(string invoiceId)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
            return ErrorResponse("invoiceId is required.");
        return OkResponse(new { id = invoiceId });
    }

    [HttpGet("{invoiceId}/download")]
    public IActionResult Download(string invoiceId)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
            return ErrorResponse("invoiceId is required.");
        return OkResponse(new { url = (string?)null });
    }
}
