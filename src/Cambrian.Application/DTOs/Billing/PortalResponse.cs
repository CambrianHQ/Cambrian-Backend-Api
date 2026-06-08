namespace Cambrian.Application.DTOs.Billing;

/// <summary>Response for <c>POST /api/billing/portal</c>.</summary>
public class PortalResponse
{
    public string PortalUrl { get; set; } = string.Empty;
}
