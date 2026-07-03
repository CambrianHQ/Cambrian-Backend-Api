using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Authorship;
using Cambrian.Application.Exceptions;
using Cambrian.Application.Interfaces;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Cambrian.Api.Services;

public interface IAuthorshipCertificatePdfService
{
    Task<AuthorshipCertificatePdfResult?> GetAsync(Guid recordId, string userId, CancellationToken ct = default);
}

public sealed record AuthorshipCertificatePdfResult(
    Stream Stream,
    long? Length,
    string StorageKey,
    bool ServedFromStorage);

public sealed class AuthorshipCertificatePdfService : IAuthorshipCertificatePdfService
{
    private const string PdfContentType = "application/pdf";
    private const string Jade = "#00FFA3";
    private const string Disclaimer =
        "This certificate documents a public authorship record created on Cambrian. It is evidence of declared human creative contribution, not a government copyright registration.";

    private readonly IAuthorshipRecordService _records;
    private readonly IPlanEntitlementService _plans;
    private readonly IObjectStorage _storage;
    private readonly ILogger<AuthorshipCertificatePdfService> _logger;

    public AuthorshipCertificatePdfService(
        IAuthorshipRecordService records,
        IPlanEntitlementService plans,
        IObjectStorage storage,
        ILogger<AuthorshipCertificatePdfService> logger)
    {
        _records = records;
        _plans = plans;
        _storage = storage;
        _logger = logger;
    }

    public async Task<AuthorshipCertificatePdfResult?> GetAsync(
        Guid recordId, string userId, CancellationToken ct = default)
    {
        var document = await _records.GetCertificateDocumentForOwnerAsync(recordId, userId, ct);
        if (document is null)
            return null;

        var entitlements = await _plans.ResolveAsync(userId, ct);
        if (!string.Equals(entitlements.Status, "active", StringComparison.OrdinalIgnoreCase)
            || !entitlements.Features.TryGetValue(PlanEntitlements.PdfCertificatesFeatureKey, out var enabled)
            || !enabled)
        {
            throw new UpgradeRequiredException(
                "Your current plan does not include PDF provenance certificates. Upgrade to Creator or Pro to unlock this feature.");
        }

        var storageKey = StorageKey(document);
        var stored = await TryOpenStoredPdfAsync(recordId, storageKey);
        if (stored is not null)
            return stored;

        var bytes = RenderPdf(document);
        await using (var upload = new MemoryStream(bytes))
        {
            await _storage.UploadAsync(upload, storageKey, PdfContentType);
        }

        _logger.LogInformation(
            "EVENT: AuthorshipCertificatePdfGenerated recordId:{RecordId} storageKey:{StorageKey} bytes:{Bytes}",
            recordId, storageKey, bytes.Length);

        return new AuthorshipCertificatePdfResult(
            new MemoryStream(bytes),
            bytes.Length,
            storageKey,
            ServedFromStorage: false);
    }

    private async Task<AuthorshipCertificatePdfResult?> TryOpenStoredPdfAsync(Guid recordId, string storageKey)
    {
        StorageFile? stored = null;
        try
        {
            stored = await _storage.OpenReadAsync(storageKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Authorship certificate storage read failed; regenerating. recordId:{RecordId} storageKey:{StorageKey}",
                recordId, storageKey);
        }

        if (stored is null)
            return null;

        if (!string.Equals(stored.ContentType, PdfContentType, StringComparison.OrdinalIgnoreCase))
        {
            stored.Dispose();
            return null;
        }

        _logger.LogInformation(
            "EVENT: AuthorshipCertificatePdfStorageHit recordId:{RecordId} storageKey:{StorageKey}",
            recordId, storageKey);

        return new AuthorshipCertificatePdfResult(
            stored.Stream,
            stored.Length,
            storageKey,
            ServedFromStorage: true);
    }

    private static string StorageKey(AuthorshipCertificateDocument document) =>
        $"certificates/{document.RecordId}/{document.Version}.pdf";

    private static byte[] RenderPdf(AuthorshipCertificateDocument document)
    {
        var qrSvg = BuildQrSvg(document.VerificationQrUrl);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken3));

                page.Header().Column(header =>
                {
                    header.Item().Row(row =>
                    {
                        row.RelativeItem().Text("CAMBRIAN").Bold().FontSize(24).FontColor(Colors.Black);
                        row.ConstantItem(190).AlignRight().Column(right =>
                        {
                            right.Item().AlignRight().Text("PROVENANCE CERTIFICATE").Bold().FontSize(12).FontColor(Colors.Black);
                            right.Item().AlignRight().Text($"Issued {FormatUtc(document.IssuedAt)}").FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                    });
                    header.Item().PaddingTop(8).Height(3).Background(Jade);
                });

                page.Content().PaddingTop(18).Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Text(document.TrackTitle).Bold().FontSize(20).FontColor(Colors.Black);
                    col.Item().Text($"Creator: {document.CreatorName}").FontSize(11).FontColor(Colors.Grey.Darken2);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(135);
                            c.RelativeColumn();
                        });

                        DetailRow(table, "Track ID", document.TrackCode);
                        DetailRow(table, "Record Hash", document.RecordHash);
                        DetailRow(table, "Anchor / Stamp", document.ChainAnchor ?? "Not anchored");
                        DetailRow(table, "Record Created", FormatUtc(document.CreatedAt));
                    });

                    col.Item().PaddingTop(4).BorderLeft(3).BorderColor(Jade).PaddingLeft(10).Column(summary =>
                    {
                        summary.Item().Text("Authorship Summary").Bold().FontSize(12).FontColor(Colors.Black);
                        summary.Item().PaddingTop(4).Text(TrimForPdf(document.AuthorshipSummary, 900)).LineHeight(1.25f);
                    });

                    col.Item().PaddingTop(4).Text("Verification").Bold().FontSize(12).FontColor(Colors.Black);
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(left =>
                        {
                            left.Item().Text($"Verify this record: {document.VerificationDisplayUrl}")
                                .FontSize(10)
                                .FontColor(Colors.Black);
                            left.Item().PaddingTop(6).Text("The hash above identifies the signed authorship record. The QR code resolves to the same verification URL.")
                                .FontSize(8)
                                .FontColor(Colors.Grey.Darken1);
                        });
                        row.ConstantItem(96).Height(96).Svg(qrSvg);
                    });

                    col.Item().PaddingTop(2).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(135);
                            c.RelativeColumn();
                        });

                        DetailRow(table, "Signature Key", document.KeyId);
                        DetailRow(table, "Algorithm", document.Algorithm);
                    });
                });

                page.Footer().AlignCenter().Text(Disclaimer).FontSize(8).FontColor(Colors.Grey.Darken1);
            });
        }).GeneratePdf();
    }

    private static void DetailRow(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(3).PaddingRight(8).Text(label).Bold().FontColor(Colors.Grey.Darken2);
        table.Cell().PaddingVertical(3).Text(value).FontColor(Colors.Black);
    }

    private static string BuildQrSvg(string url) => SimpleQrCodeSvg.Create(url);

    private static string FormatUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("MMMM d, yyyy HH:mm:ss 'UTC'");

    private static string TrimForPdf(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 3)] + "...";
    }
}
