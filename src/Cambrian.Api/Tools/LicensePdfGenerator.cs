using Cambrian.Application.DTOs.Licenses;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cambrian.Api.Tools;

/// <summary>
/// Generates a professional PDF license certificate for a purchased track.
/// </summary>
public static class LicensePdfGenerator
{
    public static byte[] Generate(LicenseCertificateDto cert, string? trackTitle = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.MarginHorizontal(50);
                page.MarginVertical(40);
                page.DefaultTextStyle(x => x.FontSize(11).FontColor(Colors.Grey.Darken3));

                page.Header().Column(col =>
                {
                    col.Item().PaddingBottom(10).BorderBottom(2).BorderColor(Colors.Blue.Darken2).Row(row =>
                    {
                        row.RelativeItem().Column(inner =>
                        {
                            inner.Item().Text("CAMBRIAN").Bold().FontSize(24).FontColor(Colors.Blue.Darken2);
                            inner.Item().Text("Music Marketplace").FontSize(10).FontColor(Colors.Grey.Medium);
                        });

                        row.ConstantItem(160).AlignRight().Column(inner =>
                        {
                            inner.Item().Text("LICENSE CERTIFICATE").Bold().FontSize(14).FontColor(Colors.Blue.Darken2);
                            inner.Item().Text($"Issued: {cert.IssuedAt:MMMM d, yyyy}").FontSize(9).FontColor(Colors.Grey.Medium);
                        });
                    });
                });

                page.Content().PaddingVertical(20).Column(col =>
                {
                    // Certificate title
                    col.Item().PaddingBottom(15).Text($"Certificate of License — {FormatLicenseType(cert.LicenseType)}")
                        .Bold().FontSize(16).FontColor(Colors.Grey.Darken4);

                    // License details table
                    col.Item().PaddingBottom(20).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.ConstantColumn(160);
                            columns.RelativeColumn();
                        });

                        AddRow(table, "License ID", cert.LicenseId);
                        AddRow(table, "Track", trackTitle ?? cert.TrackId);
                        AddRow(table, "Track ID", cert.TrackId);
                        AddRow(table, "License Type", FormatLicenseType(cert.LicenseType));
                        AddRow(table, "Usage Type", FormatUsageType(cert.UsageType));
                        AddRow(table, "Buyer ID", cert.BuyerId);
                        AddRow(table, "Creator ID", cert.CreatorId);
                        AddRow(table, "Issued At", cert.IssuedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
                    });

                    // Allowed Uses
                    if (cert.AllowedUses is { Count: > 0 })
                    {
                        col.Item().PaddingBottom(5).Text("Permitted Uses").Bold().FontSize(13).FontColor(Colors.Blue.Darken2);
                        col.Item().PaddingBottom(15).Column(inner =>
                        {
                            foreach (var use in cert.AllowedUses)
                            {
                                inner.Item().PaddingLeft(10).Row(row =>
                                {
                                    row.ConstantItem(15).Text("•").FontColor(Colors.Blue.Darken2);
                                    row.RelativeItem().Text(use);
                                });
                            }
                        });
                    }
                    else
                    {
                        col.Item().PaddingBottom(5).Text("Permitted Uses").Bold().FontSize(13).FontColor(Colors.Blue.Darken2);
                        col.Item().PaddingBottom(15).PaddingLeft(10).Text("Unrestricted — all uses permitted under this license tier.")
                            .Italic().FontColor(Colors.Grey.Darken1);
                    }

                    // Restrictions
                    if (cert.Restrictions is { Count: > 0 })
                    {
                        col.Item().PaddingBottom(5).Text("Restrictions").Bold().FontSize(13).FontColor(Colors.Red.Darken2);
                        col.Item().PaddingBottom(15).Column(inner =>
                        {
                            foreach (var restriction in cert.Restrictions)
                            {
                                inner.Item().PaddingLeft(10).Row(row =>
                                {
                                    row.ConstantItem(15).Text("•").FontColor(Colors.Red.Darken2);
                                    row.RelativeItem().Text(restriction);
                                });
                            }
                        });
                    }

                    // Legal notice
                    col.Item().PaddingTop(20).BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(10).Column(inner =>
                    {
                        inner.Item().Text("Legal Notice").Bold().FontSize(10).FontColor(Colors.Grey.Darken2);
                        inner.Item().PaddingTop(5).Text(
                            "This certificate serves as proof of license purchase through the Cambrian Music Marketplace. " +
                            "The license terms above govern the permitted use of the licensed track. " +
                            "Redistribution or resale of this license is prohibited unless explicitly permitted. " +
                            "The licensor retains all intellectual property rights not expressly granted herein.")
                            .FontSize(9).FontColor(Colors.Grey.Darken1);
                    });
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Cambrian Music Marketplace — cambrianmusic.com — ").FontSize(8).FontColor(Colors.Grey.Medium);
                    text.Span("Page ").FontSize(8).FontColor(Colors.Grey.Medium);
                    text.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                    text.Span(" of ").FontSize(8).FontColor(Colors.Grey.Medium);
                    text.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        });

        using var stream = new MemoryStream();
        document.GeneratePdf(stream);
        return stream.ToArray();
    }

    private static void AddRow(TableDescriptor table, string label, string value)
    {
        table.Cell().PaddingVertical(4).PaddingRight(10).Text(label).Bold().FontColor(Colors.Grey.Darken2);
        table.Cell().PaddingVertical(4).Text(value).FontColor(Colors.Grey.Darken4);
    }

    private static string FormatLicenseType(string? type) => type switch
    {
        "standard" => "Standard (Personal Use)",
        "non-exclusive" => "Non-Exclusive (Commercial)",
        "exclusive" => "Exclusive (Full Rights)",
        _ => type ?? "Standard"
    };

    private static string FormatUsageType(string? usage) => usage switch
    {
        "personal" => "Personal",
        "youtube" => "YouTube / Video",
        "ads" => "Advertising",
        "podcast" => "Podcast",
        "game" => "Game / Interactive",
        "film" => "Film / TV",
        "social" => "Social Media",
        _ => usage ?? "Personal"
    };
}
