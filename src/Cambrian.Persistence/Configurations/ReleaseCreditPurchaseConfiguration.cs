using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class ReleaseCreditPurchaseConfiguration : IEntityTypeConfiguration<ReleaseCreditPurchase>
{
    public void Configure(EntityTypeBuilder<ReleaseCreditPurchase> builder)
    {
        builder.ToTable("ReleaseCreditPurchases");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatorId).IsRequired().HasMaxLength(450);
        builder.Property(p => p.Pack).HasMaxLength(40);
        builder.Property(p => p.Status).HasMaxLength(20).HasDefaultValue("paid").IsRequired();
        builder.Property(p => p.StripeSessionId).HasMaxLength(255);

        // Webhook idempotency: one grant per Stripe session (mirrors Purchase).
        builder.HasIndex(p => p.StripeSessionId)
            .IsUnique()
            .HasFilter("\"StripeSessionId\" IS NOT NULL")
            .HasDatabaseName("ux_release_credit_purchases_session");

        // Balance derivation sums paid rows per creator.
        builder.HasIndex(p => p.CreatorId)
            .HasDatabaseName("ix_release_credit_purchases_creator");
    }
}
