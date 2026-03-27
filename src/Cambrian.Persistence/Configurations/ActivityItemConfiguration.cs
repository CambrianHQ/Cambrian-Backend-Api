using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class ActivityItemConfiguration : IEntityTypeConfiguration<ActivityItem>
{
    public void Configure(EntityTypeBuilder<ActivityItem> builder)
    {
        builder.ToTable("activity_items");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Type)
            .HasColumnName("type")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.IsSimulated)
            .HasColumnName("is_simulated")
            .HasDefaultValue(false);

        builder.Property(x => x.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .IsRequired();

        builder.HasIndex(x => new { x.Type, x.CreatedAtUtc })
            .HasDatabaseName("ix_activity_items_type_created");

        builder.HasIndex(x => new { x.SourceId, x.Type })
            .IsUnique()
            .HasDatabaseName("ux_activity_items_source_type");
    }
}
