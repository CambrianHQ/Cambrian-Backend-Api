using Cambrian.Api.Data;
using Cambrian.Api.Entities;
using Cambrian.Api.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Services.Implementations;

public class ApiPayoutService : IPayoutService
{
    private readonly ApplicationDbContext _db;

    public ApiPayoutService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task RequestPayout(Guid creatorId, decimal amount)
    {
        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Amount = amount,
            Status = "pending",
            RequestedAt = DateTime.UtcNow
        };

        _db.Add(payout);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<Payout>> GetHistory(Guid creatorId)
    {
        return await _db.Payouts
            .Where(p => p.CreatorId == creatorId)
            .OrderByDescending(p => p.RequestedAt)
            .ToListAsync();
    }
}
