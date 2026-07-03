using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class AuthorshipRecordConfiguration : IEntityTypeConfiguration<AuthorshipRecord>
{
    public void Configure(EntityTypeBuilder<AuthorshipRecord> builder)
    {
        builder.ToTable("AuthorshipRecords");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CreatorId).HasMaxLength(450).IsRequired();
        builder.Property(x => x.ArtistName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.EvidenceJson).IsRequired();
        builder.Property(x => x.RecordHash).HasMaxLength(64);
        builder.Property(x => x.Signature).HasMaxLength(200);
        builder.Property(x => x.SignatureAlgorithm).HasMaxLength(40);
        builder.Property(x => x.KeyId).HasMaxLength(32);
        builder.Property(x => x.StripeSessionId).HasMaxLength(255);

        builder.HasIndex(x => x.CreatorId).HasDatabaseName("IX_AuthorshipRecords_CreatorId");
        builder.HasIndex(x => x.TrackId).HasDatabaseName("IX_AuthorshipRecords_TrackId");
        builder.HasIndex(x => x.RecordHash)
            .HasFilter("\"RecordHash\" IS NOT NULL")
            .HasDatabaseName("IX_AuthorshipRecords_RecordHash");

        // One issued record per checkout session — webhook retries are no-ops.
        builder.HasIndex(x => x.StripeSessionId)
            .IsUnique()
            .HasFilter("\"StripeSessionId\" IS NOT NULL")
            .HasDatabaseName("IX_AuthorshipRecords_StripeSessionId");
    }
}
