using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class TrackVideoProofConfiguration : IEntityTypeConfiguration<TrackVideoProof>
{
    public void Configure(EntityTypeBuilder<TrackVideoProof> builder)
    {
        builder.ToTable("TrackVideoProofs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.VideoType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Url).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Visibility).HasMaxLength(20).IsRequired().HasDefaultValue("public");

        builder.HasIndex(x => new { x.TrackId, x.SortOrder })
            .HasDatabaseName("IX_TrackVideoProofs_TrackId_SortOrder");

        builder.HasOne(x => x.Track)
            .WithMany()
            .HasForeignKey(x => x.TrackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
