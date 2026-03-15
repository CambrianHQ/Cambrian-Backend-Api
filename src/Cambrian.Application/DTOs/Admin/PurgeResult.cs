namespace Cambrian.Application.DTOs.Admin;

public class PurgeResult
{
    public int UsersDeleted { get; set; }
    public int TracksDeleted { get; set; }
    public int PurchasesDeleted { get; set; }
    public int LibraryItemsDeleted { get; set; }
    public int InvoicesDeleted { get; set; }
    public int PayoutsDeleted { get; set; }
    public int SubscriptionsDeleted { get; set; }
    public int StreamSessionsDeleted { get; set; }
    public int WalletTransactionsDeleted { get; set; }
    public int WebhookEventsDeleted { get; set; }
    public int AuditLogsDeleted { get; set; }
    public int AbuseReportsDeleted { get; set; }
    public int LicenseCertificatesDeleted { get; set; }
    public string AdminPreserved { get; set; } = "";
}
