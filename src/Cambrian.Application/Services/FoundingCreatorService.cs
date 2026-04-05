using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.FoundingCreator;
using Cambrian.Application.Interfaces;
using Cambrian.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Application.Services;

public class FoundingCreatorService : IFoundingCreatorService
{
    private readonly UserManager<ApplicationUser> _userManager;

    public FoundingCreatorService(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<FoundingCreatorDto?> EnrollFoundingCreatorAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return null;

        user.IsFoundingCreator = true;
        user.FoundingCreatorEnrolledAt = DateTime.UtcNow;
        user.FoundingCreatorExpiresAt = DateTime.UtcNow.AddMonths(6);
        await _userManager.UpdateAsync(user);

        return MapToDto(user);
    }

    public async Task<List<FoundingCreatorDto>> GetFoundingCreatorsAsync()
    {
        var users = await _userManager.Users
            .Where(u => u.IsFoundingCreator)
            .OrderByDescending(u => u.FoundingCreatorEnrolledAt)
            .ToListAsync();

        return users.Select(MapToDto).ToList();
    }

    public async Task<FoundingCreatorStatusDto?> GetFoundingStatusAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return null;

        var isActive = user.IsFoundingCreator
            && user.FoundingCreatorExpiresAt.HasValue
            && user.FoundingCreatorExpiresAt.Value > DateTime.UtcNow;

        var daysRemaining = isActive
            ? (int)(user.FoundingCreatorExpiresAt!.Value - DateTime.UtcNow).TotalDays
            : 0;

        var feeRate = isActive ? 0m : TierManifest.For(user.CreatorTier).FeeRate;

        return new FoundingCreatorStatusDto
        {
            IsFoundingCreator = user.IsFoundingCreator,
            EnrolledAt = user.FoundingCreatorEnrolledAt,
            ExpiresAt = user.FoundingCreatorExpiresAt,
            DaysRemaining = daysRemaining,
            CurrentFeeRate = feeRate
        };
    }

    private static FoundingCreatorDto MapToDto(ApplicationUser user)
    {
        var daysRemaining = user.FoundingCreatorExpiresAt.HasValue
            && user.FoundingCreatorExpiresAt.Value > DateTime.UtcNow
            ? (int?)(user.FoundingCreatorExpiresAt.Value - DateTime.UtcNow).TotalDays
            : null;

        return new FoundingCreatorDto
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            IsFoundingCreator = user.IsFoundingCreator,
            EnrolledAt = user.FoundingCreatorEnrolledAt,
            ExpiresAt = user.FoundingCreatorExpiresAt,
            DaysRemaining = daysRemaining
        };
    }
}
