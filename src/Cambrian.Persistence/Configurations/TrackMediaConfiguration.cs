using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class TrackMediaConfiguration : IEntityTypeConfiguration<TrackMedia>
{
    public void Configure(EntityTypeBuilder<TrackMedia> builder)
    {
        builder.ToTable("TrackMedia");
        builder.HasKey(x => x.TrackId);
        builder.Property(x => x.ObjectKey).HasMaxLength(1_024);
        builder.Property(x => x.State).HasMaxLength(32).IsRequired().HasDefaultValue(TrackMediaStates.Draft);
        builder.Property(x => x.FailureCode).HasMaxLength(64);
        builder.Property(x => x.FailureDetail).HasMaxLength(1_000);
        builder.Property(x => x.ContentType).HasMaxLength(128);
        builder.Property(x => x.ChecksumSha256).HasMaxLength(64);
        builder.Property(x => x.ValidationVersion).HasMaxLength(32);
        builder.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        builder.HasIndex(x => x.ObjectKey).HasDatabaseName("ix_track_media_object_key");
        builder.HasIndex(x => new { x.State, x.ValidatedAtUtc }).HasDatabaseName("ix_track_media_state_validated");
        builder.HasOne(x => x.Track)
            .WithOne(x => x.Media)
            .HasForeignKey<TrackMedia>(x => x.TrackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
