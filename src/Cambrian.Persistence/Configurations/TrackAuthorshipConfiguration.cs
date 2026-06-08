using Cambrian.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cambrian.Persistence.Configurations;

public sealed class TrackAuthorshipConfiguration : IEntityTypeConfiguration<TrackAuthorship>
{
    public void Configure(EntityTypeBuilder<TrackAuthorship> builder)
    {
        builder.ToTable("TrackAuthorships");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Edits)
            .HasMaxLength(4000);

        builder.Property(x => x.ArrangementNotes)
            .HasMaxLength(4000);

        builder.Property(x => x.ProcessNotes)
            .HasMaxLength(4000);

        builder.Property(x => x.AiDisclosure)
            .HasMaxLength(4000);

        // One authorship document per track (upsert target).
        builder.HasIndex(x => x.TrackId)
            .IsUnique()
            .HasDatabaseName("IX_TrackAuthorships_TrackId");

        builder.HasOne(x => x.Track)
            .WithMany()
            .HasForeignKey(x => x.TrackId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
