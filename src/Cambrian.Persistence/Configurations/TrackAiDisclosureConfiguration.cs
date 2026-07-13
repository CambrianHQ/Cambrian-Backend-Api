using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class TrackAiDisclosureConfiguration : IEntityTypeConfiguration<TrackAiDisclosure>
{
    public void Configure(EntityTypeBuilder<TrackAiDisclosure> b)
    {
        b.ToTable("TrackAiDisclosures");
        b.HasKey(x => x.TrackId);
        b.Property(x => x.Classification).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(x => x.GeneratorTool).HasMaxLength(200);
        b.Property(x => x.ModelVersion).HasMaxLength(200);
        b.Property(x => x.CommercialUseLicenseBasis).HasMaxLength(2000);
        b.Property(x => x.VoiceLikenessAuthorization).HasMaxLength(2000);
        b.Property(x => x.HumanContributionNarrative).HasMaxLength(5000);
        b.Property(x => x.CorrectionReason).HasMaxLength(1000);
        b.Property(x => x.CreatedByUserId).HasMaxLength(450).IsRequired();
        b.Property(x => x.UpdatedByUserId).HasMaxLength(450).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasOne(x => x.Track).WithOne().HasForeignKey<TrackAiDisclosure>(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TrackAiDisclosureRevisionConfiguration : IEntityTypeConfiguration<TrackAiDisclosureRevision>
{
    public void Configure(EntityTypeBuilder<TrackAiDisclosureRevision> b)
    {
        b.ToTable("TrackAiDisclosureRevisions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.SnapshotJson).IsRequired();
        b.Property(x => x.ChangedByUserId).HasMaxLength(450).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.HasIndex(x => new { x.TrackId, x.Version }).IsUnique();
        b.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
    }
}
