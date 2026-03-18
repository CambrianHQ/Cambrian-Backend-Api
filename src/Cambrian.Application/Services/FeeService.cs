using Cambrian.Application.Configuration;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Enums;

namespace Cambrian.Application.Services;

public sealed class FeeService : IFeeService
{
    public int FreeUploadLimit => TierManifest.Free.UploadLimit ?? 0;

    public decimal GetPlatformFeeRate(CreatorTier tier) =>
        TierManifest.For(tier).FeeRate;

    public decimal GetPlatformFeeRate(string tierString) =>
        TierManifest.For(tierString).FeeRate;
}
