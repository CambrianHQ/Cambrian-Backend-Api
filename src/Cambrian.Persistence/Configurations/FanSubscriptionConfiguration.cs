using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class FanSubscriptionConfiguration : IEntityTypeConfiguration<FanSubscription>
{
    public void Configure(EntityTypeBuilder<FanSubscription> builder)
    {
        builder.ToTable("FanSubscriptions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FanUserId).HasMaxLength(450).IsRequired();
        builder.Property(x => x.ArtistUserId).HasMaxLength(450).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.StripeSubscriptionId).HasMaxLength(255);
        builder.Property(x => x.StripeSessionId).HasMaxLength(255);

        builder.HasIndex(x => x.ArtistUserId).HasDatabaseName("IX_FanSubscriptions_ArtistUserId");
        builder.HasIndex(x => x.FanUserId).HasDatabaseName("IX_FanSubscriptions_FanUserId");
        builder.HasIndex(x => x.StripeSubscriptionId).HasDatabaseName("IX_FanSubscriptions_StripeSubscriptionId");

        // One row per originating checkout session — webhook retries are no-ops.
        builder.HasIndex(x => x.StripeSessionId)
            .IsUnique()
            .HasFilter("\"StripeSessionId\" IS NOT NULL")
            .HasDatabaseName("IX_FanSubscriptions_StripeSessionId");
    }
}
