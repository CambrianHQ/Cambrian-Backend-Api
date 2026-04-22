namespace Cambrian.Application.Interfaces;

/// <summary>
/// Runs the hard deployment gate probes (DB, Storage, Stripe) and reports a
/// structured result. The controller is a thin adapter — all probe logic
/// lives here so governance keeps DbContext and business logic out of the
/// HTTP layer.
/// </summary>
public interface IPreflightService
{
    Task<PreflightResult> RunAsync(CancellationToken ct);
}

public sealed class PreflightResult
{
    public bool Degraded { get; init; }
    public object Body { get; init; } = new();
}
