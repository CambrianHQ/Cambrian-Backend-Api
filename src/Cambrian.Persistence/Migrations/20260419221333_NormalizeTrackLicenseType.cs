using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Track.LicenseType is supposed to be the *listing* tier (non-exclusive,
    /// exclusive, copyright_buyout). The frontend's upload form was writing a
    /// usage-type value ("personal") into this column instead, which the
    /// marketplace UI reads as "not for sale" and renders as "Coming soon".
    /// All ~50 production tracks ended up with LicenseType = 'personal' and were
    /// unsellable as a result.
    ///
    /// Normalize any non-listing value to 'non-exclusive' (the floor tier).
    /// Tracks that legitimately have 'exclusive' or 'copyright_buyout' set are
    /// left alone. After this runs creators can still change their listing tier
    /// via PUT /tracks/{id}, which only accepts the price columns — so this
    /// won't be re-clobbered by edits.
    /// </summary>
    public partial class NormalizeTrackLicenseType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Tracks""
                SET ""LicenseType"" = 'non-exclusive'
                WHERE ""LicenseType"" IS NULL
                   OR ""LicenseType"" NOT IN ('non-exclusive', 'exclusive', 'copyright_buyout');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op. Reversing this would re-strand prod tracks in the broken
            // state and we don't have the original per-row values.
        }
    }
}
