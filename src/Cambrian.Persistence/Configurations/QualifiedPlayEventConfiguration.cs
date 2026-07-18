using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class QualifiedPlayEventConfiguration : IEntityTypeConfiguration<QualifiedPlayEvent>
{
    public void Configure(EntityTypeBuilder<QualifiedPlayEvent> builder)
    {
        builder.ToTable("QualifiedPlayEvents");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.CreatorId).IsRequired().HasMaxLength(450);
        builder.Property(x => x.ListenerUserId).HasMaxLength(450);
        builder.Property(x => x.ListenerKeyHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.AnonymousSessionHash).HasMaxLength(64);
        builder.Property(x => x.QualificationBasis).IsRequired().HasMaxLength(64);
        builder.Property(x => x.QualifiedAtUtc).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_qualified_play_events_idempotency_key");
        builder.HasIndex(x => x.PlaybackSessionId)
            .IsUnique()
            .HasDatabaseName("ux_qualified_play_events_playback_session");
        builder.HasIndex(x => new { x.TrackId, x.QualifiedAtUtc })
            .HasDatabaseName("ix_qualified_play_events_track_qualified_at");
        builder.HasIndex(x => new { x.ListenerKeyHash, x.TrackId, x.QualifiedAtUtc })
            .HasDatabaseName("ix_qualified_play_events_listener_track_qualified_at");
        builder.HasIndex(x => x.AggregatedAtUtc)
            .HasDatabaseName("ix_qualified_play_events_aggregated_at");

        builder.HasOne(x => x.Track)
            .WithMany()
            .HasForeignKey(x => x.TrackId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.PlaybackSession)
            .WithMany()
            .HasForeignKey(x => x.PlaybackSessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
