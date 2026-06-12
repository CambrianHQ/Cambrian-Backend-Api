using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class EarningsTransactionConfiguration : IEntityTypeConfiguration<EarningsTransaction>
{
    public void Configure(EntityTypeBuilder<EarningsTransaction> builder)
    {
        builder.ToTable("EarningsTransactions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ArtistUserId).HasMaxLength(450).IsRequired();
        builder.Property(x => x.Source).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(8).IsRequired();
        builder.Property(x => x.ExternalRef).HasMaxLength(255).IsRequired();
        builder.Property(x => x.PayerUserId).HasMaxLength(450);

        // Read-side aggregation path (owned by the earnings read agent).
        builder.HasIndex(x => new { x.ArtistUserId, x.CreatedAt })
            .HasDatabaseName("IX_EarningsTransactions_Artist_CreatedAt");
        builder.HasIndex(x => new { x.ArtistUserId, x.Source })
            .HasDatabaseName("IX_EarningsTransactions_Artist_Source");

        // Append-only idempotency: one row per money event per source.
        builder.HasIndex(x => new { x.Source, x.ExternalRef })
            .IsUnique()
            .HasDatabaseName("IX_EarningsTransactions_Source_ExternalRef");
    }
}
