using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class TrackStatConfiguration : IEntityTypeConfiguration<TrackStat>
{
    public void Configure(EntityTypeBuilder<TrackStat> builder)
    {
        builder.ToTable("TrackStats");

        // The primary key IS the foreign key — strict 1:1 with Track.
        builder.HasKey(x => x.TrackId);

        builder.Property(x => x.PlayCount).HasDefaultValue(0L);
        builder.Property(x => x.UniqueListenerCount).HasDefaultValue(0L);
        builder.Property(x => x.LikeCount).HasDefaultValue(0);
        builder.Property(x => x.SalesCount).HasDefaultValue(0);
        builder.Property(x => x.TipCount).HasDefaultValue(0);
        builder.Property(x => x.TipTotalCents).HasDefaultValue(0L);
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasOne(x => x.Track)
            .WithOne()
            .HasForeignKey<TrackStat>(x => x.TrackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
