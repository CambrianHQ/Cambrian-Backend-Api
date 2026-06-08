namespace Cambrian.Application.Configuration;

/// <summary>
/// Configuration for the batched on-chain anchor (bound from the <c>Provenance:Anchor</c> section).
/// </summary>
public sealed class ProvenanceAnchorOptions
{
    public const string SectionName = "Provenance:Anchor";

    /// <summary>Anchor implementation: <c>noop</c> (dev/default, no chain write) | <c>evm</c> (Base L2).</summary>
    public string Provider { get; set; } = "noop";

    /// <summary>When true, the scheduled batch worker runs. Off by default (opt-in per environment).</summary>
    public bool JobEnabled { get; set; }

    /// <summary>Seconds between batch ticks (clamped to a sane minimum at runtime).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Maximum tracks anchored per batch (all share one root + one tx).</summary>
    public int MaxBatchSize { get; set; } = 500;

    // ── EVM provider settings (used when Provider == "evm") ──

    /// <summary>Chain label recorded on the anchor (e.g. <c>base</c>, <c>base-sepolia</c>).</summary>
    public string Chain { get; set; } = "base";

    /// <summary>EVM chain id (8453 = Base mainnet, 84532 = Base Sepolia).</summary>
    public int ChainId { get; set; } = 8453;

    /// <summary>JSON-RPC endpoint URL for the L2.</summary>
    public string? RpcUrl { get; set; }

    /// <summary>Server-only hot-wallet private key that signs the anchor transactions. Never exposed.</summary>
    public string? PrivateKey { get; set; }
}
