using Cambrian.Application.DTOs.Invoices;
using Cambrian.Application.Interfaces;

namespace Cambrian.Application.Services;

public class InvoiceService : IInvoiceService
{
    private readonly IInvoiceRepository _invoices;

    public InvoiceService(IInvoiceRepository invoices)
    {
        _invoices = invoices;
    }

    public async Task<IReadOnlyCollection<InvoiceResponse>> GetByUserAsync(string userId)
    {
        var invoices = await _invoices.GetByUserIdAsync(userId);
        return invoices.Select(i => new InvoiceResponse
        {
            Id = i.Id.ToString(),
            PurchaseId = i.PurchaseId.ToString(),
            AmountCents = i.AmountCents,
            Currency = i.Currency,
            Status = i.Status,
            IssuedAt = i.IssuedAt,
            PaidAt = i.PaidAt
        }).ToList();
    }

    public async Task<InvoiceResponse?> GetByIdAsync(string invoiceId, string userId)
    {
        if (!Guid.TryParse(invoiceId, out var id))
            return null;

        var invoice = await _invoices.GetByIdAsync(id);
        if (invoice is null || invoice.UserId != userId)
            return null;

        return new InvoiceResponse
        {
            Id = invoice.Id.ToString(),
            PurchaseId = invoice.PurchaseId.ToString(),
            AmountCents = invoice.AmountCents,
            Currency = invoice.Currency,
            Status = invoice.Status,
            IssuedAt = invoice.IssuedAt,
            PaidAt = invoice.PaidAt
        };
    }

    public Task<byte[]?> DownloadAsync(string invoiceId, string userId)
    {
        // PDF generation will be implemented later
        return Task.FromResult<byte[]?>(null);
    }
}
