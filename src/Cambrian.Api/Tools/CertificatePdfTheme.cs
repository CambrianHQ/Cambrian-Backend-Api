using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cambrian.Api.Tools;

/// <summary>
/// Shared QuestPDF "chrome" for Cambrian certificate documents — page defaults,
/// branded header, footer, two-column detail rows, and bullet sections.
///
/// Extracted from the original license-certificate generator so that certificate
/// generators reuse one look-and-feel instead of duplicating layout. This is the
/// reusable scaffolding the provenance certificate is designed to build on
/// (see <c>docs/RELEASE_READY_PLAN.md</c> §9). It contains no license-specific
/// content — callers supply their own titles, rows, and sections.
/// </summary>
public static class CertificatePdfTheme
{
    public static readonly string BrandPrimary = Colors.Blue.Darken2;

    /// <summary>Apply Cambrian's standard certificate page size, margins, and base text style.</summary>
    public static void ApplyPageDefaults(PageDescriptor page)
    {
        page.Size(PageSizes.Letter);
        page.MarginHorizontal(50);
        page.MarginVertical(40);
        page.DefaultTextStyle(x => x.FontSize(11).FontColor(Colors.Grey.Darken3));
    }

    /// <summary>
    /// Branded header: the "CAMBRIAN" wordmark + subtitle on the left, a document
    /// title + issued date on the right, beneath a brand-colored rule.
    /// </summary>
    public static void Header(IContainer container, string subtitle, string documentTitle, DateTime issuedAt)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(10).BorderBottom(2).BorderColor(BrandPrimary).Row(row =>
            {
                row.RelativeItem().Column(inner =>
                {
                    inner.Item().Text("CAMBRIAN").Bold().FontSize(24).FontColor(BrandPrimary);
                    inner.Item().Text(subtitle).FontSize(10).FontColor(Colors.Grey.Medium);
                });

                row.ConstantItem(180).AlignRight().Column(inner =>
                {
                    inner.Item().Text(documentTitle).Bold().FontSize(14).FontColor(BrandPrimary);
                    inner.Item().Text($"Issued: {issuedAt:MMMM d, yyyy}").FontSize(9).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }

    /// <summary>Centered footer with a site/legal line and "Page X of Y" numbering.</summary>
    public static void Footer(IContainer container, string siteLine)
    {
        container.AlignCenter().Text(text =>
        {
            text.Span($"{siteLine} — ").FontSize(8).FontColor(Colors.Grey.Medium);
            text.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
            text.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
            text.Span(" of ").FontSize(8).FontColor(Colors.Grey.Medium);
            text.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
        });
    }

    /// <summary>Add a label/value row to a two-column detail table.</summary>
    public static void DetailRow(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(4).PaddingRight(10).Text(label).Bold().FontColor(Colors.Grey.Darken2);
        table.Cell().PaddingVertical(4).Text(value).FontColor(Colors.Grey.Darken4);
    }

    /// <summary>Render a titled, color-coded bullet list section.</summary>
    public static void BulletSection(ColumnDescriptor col, string title, string titleColor, IEnumerable<string> items, string bulletColor)
    {
        col.Item().PaddingBottom(5).Text(title).Bold().FontSize(13).FontColor(titleColor);
        col.Item().PaddingBottom(15).Column(inner =>
        {
            foreach (var item in items)
            {
                inner.Item().PaddingLeft(10).Row(row =>
                {
                    row.ConstantItem(15).Text("•").FontColor(bulletColor);
                    row.RelativeItem().Text(item);
                });
            }
        });
    }
}
