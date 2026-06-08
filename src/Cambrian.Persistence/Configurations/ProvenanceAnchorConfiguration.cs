using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class ProvenanceAnchorConfiguration : IEntityTypeConfiguration<ProvenanceAnchor>
{
    public void Configure(EntityTypeBuilder<ProvenanceAnchor> builder)
    {
        builder.ToTable("ProvenanceAnchors");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ContentHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasMaxLength(20)
            .HasDefaultValue("pending")
            .IsRequired();

        builder.Property(x => x.Chain)
            .HasMaxLength(50);

        builder.Property(x => x.MerkleRoot)
            .HasMaxLength(128);

        builder.Property(x => x.RootTxRef)
            .HasMaxLength(200);

        // JSON-encoded sibling path — small but unbounded leaf count, keep generous.
        builder.Property(x => x.MerkleProof)
            .HasMaxLength(8000);

        builder.Property(x => x.FailureReason)
            .HasMaxLength(1000);

        // One anchor row per track.
        builder.HasIndex(x => x.TrackId)
            .IsUnique()
            .HasDatabaseName("IX_ProvenanceAnchors_TrackId");

        // Batch job worklist: pull pending rows, then group/update by batch.
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_ProvenanceAnchors_Status");
        builder.HasIndex(x => x.BatchId)
            .HasDatabaseName("IX_ProvenanceAnchors_BatchId");

        builder.HasOne(x => x.Track)
            .WithMany()
            .HasForeignKey(x => x.TrackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
