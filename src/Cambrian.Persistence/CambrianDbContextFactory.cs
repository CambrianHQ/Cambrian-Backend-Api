using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cambrian.Persistence;

/// <summary>
/// Used only by EF Core tooling (dotnet ef migrations add / update) at design time.
/// Not referenced at runtime.
/// </summary>
public class CambrianDbContextFactory : IDesignTimeDbContextFactory<CambrianDbContext>
{
    public CambrianDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseNpgsql("Host=localhost;Database=cambrian_dev;Username=postgres;Password=postgres")
            .Options;
        return new CambrianDbContext(opts);
    }
}
