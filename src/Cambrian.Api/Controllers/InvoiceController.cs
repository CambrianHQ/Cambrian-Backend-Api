using System.Security.Claims;
using Cambrian.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[Route("invoices")]
[Authorize]
public class InvoiceController : BaseController
{
    private readonly IInvoiceService _invoices;

    public InvoiceController(IInvoiceService invoices)
    {
        _invoices = invoices;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var invoices = await _invoices.GetByUserAsync(userId);
        return OkResponse(invoices);
    }

    [HttpGet("{invoiceId}")]
    public async Task<IActionResult> Get(string invoiceId)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
            return ErrorResponse("invoiceId is required.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var invoice = await _invoices.GetByIdAsync(invoiceId, userId);
        if (invoice is null)
            return NotFoundResponse("Invoice not found.");

        return OkResponse(invoice);
    }

    [HttpGet("{invoiceId}/download")]
    public async Task<IActionResult> Download(string invoiceId)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
            return ErrorResponse("invoiceId is required.");

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var data = await _invoices.DownloadAsync(invoiceId, userId);
        if (data is null)
        {
            // Return a URL placeholder until PDF generation is implemented
            return OkResponse(new { url = (string?)null, message = "Invoice download not yet available." });
        }

        return File(data, "application/pdf", $"invoice-{invoiceId}.pdf");
    }
}
