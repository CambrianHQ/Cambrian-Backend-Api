using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class MasteringJobConfiguration : IEntityTypeConfiguration<MasteringJob>
{
    public void Configure(EntityTypeBuilder<MasteringJob> builder)
    {
        builder.ToTable("MasteringJobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CreatorId).IsRequired();
        builder.Property(x => x.Engine).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceKey).IsRequired();
        builder.Property(x => x.SourceFileName).HasMaxLength(512);
        builder.Property(x => x.CoverArtKey).HasMaxLength(512);
        builder.Property(x => x.EngineRef).HasMaxLength(256);
        builder.Property(x => x.Kind).HasMaxLength(20).IsRequired().HasDefaultValue("mastering");
        builder.Property(x => x.ContentHash).HasMaxLength(64);
        builder.Property(x => x.Stage).HasMaxLength(20);
        builder.Property(x => x.CreditSource).HasMaxLength(20);

        // Release-pipeline idempotency: find an existing job for the same audio.
        builder.HasIndex(x => new { x.TrackId, x.ContentHash })
            .HasDatabaseName("IX_MasteringJobs_TrackId_ContentHash");

        // Worker poll: claim queued jobs oldest-first.
        builder.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("IX_MasteringJobs_Status_CreatedAt");

        // Worker recovery: find expired processing leases without scanning every job.
        builder.HasIndex(x => new { x.Status, x.ProcessingLeaseExpiresAt })
            .HasDatabaseName("IX_MasteringJobs_Status_ProcessingLeaseExpiresAt");

        // Classic upload idempotency: coalesce repeat submits from the same creator/content.
        builder.HasIndex(x => new { x.CreatorId, x.Kind, x.ContentHash })
            .HasDatabaseName("IX_MasteringJobs_Creator_Kind_ContentHash");

        builder.HasIndex(x => x.CreatorId)
            .HasDatabaseName("IX_MasteringJobs_CreatorId");

        // Credit accounting derives monthly usage from charged jobs.
        builder.HasIndex(x => new { x.CreatorId, x.ChargedAt })
            .HasDatabaseName("IX_MasteringJobs_CreatorId_ChargedAt");

        // No real FK to Tracks: TrackId is optional and ad-hoc uploads have none.
        builder.HasIndex(x => x.TrackId)
            .HasDatabaseName("IX_MasteringJobs_TrackId");
    }
}
