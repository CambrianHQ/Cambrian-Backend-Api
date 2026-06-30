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
        var connectionString = ResolveConnectionString();
        var opts = new DbContextOptionsBuilder<CambrianDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new CambrianDbContext(opts);
    }

    private static string ResolveConnectionString()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Database=cambrian_dev;Username=postgres;Password=postgres";

        if (connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(connectionString);
            var userInfo = uri.UserInfo.Split(':', 2);
            var port = uri.Port > 0 ? uri.Port : 5432;
            var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
            connectionString =
                $"Host={uri.Host};Port={port};Database={uri.AbsolutePath.TrimStart('/')};"
                + $"Username={Uri.UnescapeDataString(userInfo[0])};Password={password};"
                + "SSL Mode=Require;Trust Server Certificate=false";
        }

        return connectionString;
    }
}
