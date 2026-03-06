using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("invoices")]
[Authorize]
public class InvoicesController : BaseController
{
    [HttpGet]
    public IActionResult List([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
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
