namespace Cambrian.Application.DTOs.Health;

public class HealthStatusResponse
{
    public string Status { get; set; } = "ok";

    public DateTime Timestamp { get; set; }

    public string Environment { get; set; } = "";

    public string Database { get; set; } = "";
}
