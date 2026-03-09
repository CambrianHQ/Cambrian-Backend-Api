namespace Cambrian.Application.DTOs.Admin;

public class IntegrityReport
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public int TotalViolations => Violations.Count;

    public List<IntegrityViolation> Violations { get; set; } = new();

    public IntegritySummary Summary { get; set; } = new();
}

public class IntegrityViolation
{
    public string Rule { get; set; } = "";

    public string Severity { get; set; } = "warning";

    public string EntityType { get; set; } = "";

    public string EntityId { get; set; } = "";

    public string Description { get; set; } = "";
}

public class IntegritySummary
{
    public int CompletedPurchasesWithoutLibrary { get; set; }

    public int ExclusiveSoldButBrowsable { get; set; }

    public int PayoutAmountMismatches { get; set; }

    public int OrphanedLibraryItems { get; set; }

    public int PurchasesWithoutInvoice { get; set; }

    public int ExclusivePurchasesWithoutFlag { get; set; }

    public int WalletCreditMismatches { get; set; }
}
