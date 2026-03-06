namespace Cambrian.Application.DTOs.Admin;

public class AdminDashboardSummary
{
    public double TotalUsers { get; set; }

    public double ActiveCreators { get; set; }

    public double TracksUploaded { get; set; }

    public double LicensesSold { get; set; }

    public double TotalRevenue { get; set; }

    public double PendingPayouts { get; set; }
}
