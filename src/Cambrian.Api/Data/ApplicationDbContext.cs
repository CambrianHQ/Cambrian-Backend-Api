using Cambrian.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cambrian.Api.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<License> Licenses => Set<License>();

    public DbSet<Purchase> Purchases => Set<Purchase>();

    public DbSet<LibraryItem> Library => Set<LibraryItem>();

    public DbSet<Payout> Payouts => Set<Payout>();

    public DbSet<StreamEvent> Streams => Set<StreamEvent>();

    public DbSet<CreatorBalance> CreatorBalances => Set<CreatorBalance>();

    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();

    public DbSet<Download> Downloads => Set<Download>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
}
