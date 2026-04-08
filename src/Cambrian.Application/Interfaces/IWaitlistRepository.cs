using Cambrian.Domain.Entities;

namespace Cambrian.Application.Interfaces;

public interface IWaitlistRepository
{
    /// <summary>Look up a signup by normalized (lowercase, trimmed) email.</summary>
    Task<WaitlistSignup?> GetByEmailAsync(string normalizedEmail);

    /// <summary>Insert a new signup. Caller is responsible for normalization.</summary>
    Task AddAsync(WaitlistSignup signup);
}
