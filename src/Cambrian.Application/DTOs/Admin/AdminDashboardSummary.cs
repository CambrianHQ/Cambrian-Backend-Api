namespace Cambrian.Application.DTOs.Admin;

public class AdminDashboardSummary
{
    public int TotalUsers { get; set; }

    public int ActiveCreators { get; set; }

    public int TracksUploaded { get; set; }

    public int LicensesSold { get; set; }

    public double TotalRevenue { get; set; }

    public double PendingPayouts { get; set; }
}
