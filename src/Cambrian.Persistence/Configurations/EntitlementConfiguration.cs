using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class EntitlementConfiguration : IEntityTypeConfiguration<Entitlement>
{
    public void Configure(EntityTypeBuilder<Entitlement> builder)
    {
        builder.ToTable("Entitlements");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(x => x.ResourceType)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.ResourceId)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.AccessLevel)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.SourceType)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(x => x.SourceId)
            .HasMaxLength(200);

        builder.Property(x => x.RevokedReason)
            .HasMaxLength(500);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Access-check hot path: lookup by (user, resourceType, resourceId).
        builder.HasIndex(x => new { x.UserId, x.ResourceType, x.ResourceId })
            .HasDatabaseName("IX_Entitlements_User_Resource");

        // Admin audit: list by source (e.g. "which entitlements did this purchase create?").
        builder.HasIndex(x => new { x.SourceType, x.SourceId })
            .HasDatabaseName("IX_Entitlements_Source");
    }
}
