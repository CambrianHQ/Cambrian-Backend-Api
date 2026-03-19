using Cambrian.Domain.Enums;

namespace Cambrian.Application.Interfaces;

public interface IFeeService
{
    /// <summary>Returns the platform fee rate (0.35 = 35%, 0.15 = 15%) for the given creator tier.</summary>
    decimal GetPlatformFeeRate(CreatorTier tier);

    /// <summary>Returns the platform fee rate for the given tier string (backward compat).</summary>
    decimal GetPlatformFeeRate(string tierString);

    /// <summary>Maximum number of tracks a free-tier creator can upload.</summary>
    int FreeUploadLimit { get; }
}
