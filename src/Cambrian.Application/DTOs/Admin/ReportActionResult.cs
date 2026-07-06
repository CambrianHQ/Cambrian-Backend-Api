namespace Cambrian.Application.DTOs.Admin;

public enum ReportActionOutcome
{
    NotFound,
    InvalidState,
    Success,
}

public class ReportActionResult
{
    public ReportActionOutcome Outcome { get; set; }

    public AdminAbuseReport? Report { get; set; }

    public string Message { get; set; } = string.Empty;
}
