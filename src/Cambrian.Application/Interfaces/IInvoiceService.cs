using Cambrian.Application.DTOs.Invoices;

namespace Cambrian.Application.Interfaces;

public interface IInvoiceService
{
    Task<IReadOnlyCollection<InvoiceResponse>> GetByUserAsync(string userId);

    Task<InvoiceResponse?> GetByIdAsync(string invoiceId, string userId);

    Task<byte[]?> DownloadAsync(string invoiceId, string userId);
}
