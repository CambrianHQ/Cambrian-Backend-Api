using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class CreatorStatConfiguration : IEntityTypeConfiguration<CreatorStat>
{
    public void Configure(EntityTypeBuilder<CreatorStat> builder)
    {
        builder.ToTable("CreatorStats");

        // The primary key IS the foreign key — strict 1:1 with Creator.
        builder.HasKey(x => x.CreatorId);

        builder.Property(x => x.FollowerCount).HasDefaultValue(0);
        builder.Property(x => x.TrackCount).HasDefaultValue(0);
        builder.Property(x => x.TotalPlays).HasDefaultValue(0L);
        builder.Property(x => x.UniqueListenerCount).HasDefaultValue(0L);
        builder.Property(x => x.MonthlyPlays).HasDefaultValue(0L);
        builder.Property(x => x.SubscriberCount).HasDefaultValue(0);
        builder.Property(x => x.TipCount).HasDefaultValue(0);
        builder.Property(x => x.TipsReceivedCents).HasDefaultValue(0L);
        builder.Property(x => x.TrendingScore).HasDefaultValue(0m);
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Featured/trending creator lists order by these — index the hot columns.
        builder.HasIndex(x => x.TrendingScore).HasDatabaseName("IX_CreatorStats_TrendingScore");
        builder.HasIndex(x => x.FollowerCount).HasDatabaseName("IX_CreatorStats_FollowerCount");

        builder.HasOne(x => x.Creator)
            .WithOne()
            .HasForeignKey<CreatorStat>(x => x.CreatorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
