using Cambrian.Api.Tools;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Cambrian.Api.Tests;

/// <summary>
/// Smoke test for the reusable certificate PDF scaffolding (<see cref="CertificatePdfTheme"/>)
/// extracted from the former license PDF generator. Proves the shared QuestPDF chrome still
/// renders a valid PDF after the license-specific generator was removed — this is the reusable
/// infrastructure the provenance certificate is designed to build on (RELEASE_READY_PLAN.md §9).
/// </summary>
public sealed class CertificatePdfThemeTests
{
    [Fact]
    public void Theme_RendersNonEmptyPdf_UsingSharedChrome()
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                CertificatePdfTheme.ApplyPageDefaults(page);
                CertificatePdfTheme.Header(page.Header(), "Provenance Certificate", "CERTIFICATE", new DateTime(2026, 5, 31));

                page.Content().Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.ConstantColumn(160); c.RelativeColumn(); });
                        CertificatePdfTheme.DetailRow(table, "Track", "Test Track");
                        CertificatePdfTheme.DetailRow(table, "Creator", "DJ Test");
                    });
                    CertificatePdfTheme.BulletSection(
                        col, "Notes", CertificatePdfTheme.BrandPrimary,
                        new[] { "First note", "Second note" }, CertificatePdfTheme.BrandPrimary);
                });

                CertificatePdfTheme.Footer(page.Footer(), "cambrianmusic.com");
            });
        }).GeneratePdf();

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0, "Theme should render a non-empty PDF.");
        // PDF files start with the "%PDF" magic bytes.
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }
}
