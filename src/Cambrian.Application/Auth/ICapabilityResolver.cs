using Cambrian.Domain.Entities;

namespace Cambrian.Application.Auth;

/// <summary>
/// Resolves the set of capabilities for an authenticated user.
/// </summary>
public interface ICapabilityResolver
{
    /// <summary>
    /// Resolve all capabilities for the given user.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveAsync(ApplicationUser user);
}
