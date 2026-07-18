using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class MediaReconciliationRunConfiguration : IEntityTypeConfiguration<MediaReconciliationRun>
{
    public void Configure(EntityTypeBuilder<MediaReconciliationRun> builder)
    {
        builder.ToTable("MediaReconciliationRuns");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.FailureCode).HasMaxLength(64);
        builder.HasIndex(x => x.StartedAtUtc).HasDatabaseName("ix_media_reconciliation_runs_started");
    }
}

public sealed class MediaReconciliationFindingConfiguration : IEntityTypeConfiguration<MediaReconciliationFinding>
{
    public void Configure(EntityTypeBuilder<MediaReconciliationFinding> builder)
    {
        builder.ToTable("MediaReconciliationFindings");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FindingType).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Severity).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ObjectKey).HasMaxLength(1_024);
        builder.Property(x => x.Detail).HasMaxLength(1_000).IsRequired();
        builder.Property(x => x.Resolution).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => new { x.RunId, x.TrackId }).HasDatabaseName("ix_media_findings_run_track");
        builder.HasOne(x => x.Run)
            .WithMany(x => x.Findings)
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
