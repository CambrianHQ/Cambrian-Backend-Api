using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Data-fix migration: strips expired S3/R2 presigned URL query parameters
    /// from image/audio URL columns, leaving only the bare object key.
    ///
    /// Root cause: when Storage:PublicUrl was not configured, GetPublicUrl()
    /// previously returned a presigned URL (with X-Amz-* query params) that
    /// expired after 15 minutes.  These expired URLs were stored in the DB
    /// and broke image/audio resolution.
    ///
    /// The code fix (returning bare keys) is already deployed — this migration
    /// cleans up rows that were written before the fix.
    ///
    /// Logic per column:
    ///   1. Only touches rows where the column contains '?X-Amz-' (presigned signature).
    ///   2. For full URLs (https://...), extracts the path after '/{bucket}/' and strips
    ///      the query string — yielding the bare object key (e.g. "avatars/abc.jpg").
    ///   3. For non-URL values that somehow contain '?X-Amz-', strips the query string.
    /// </summary>
    public partial class FixExpiredPresignedImageUrls : Migration
    {
        // language=sql
        private const string FixColumnSql = """
            UPDATE "{0}"
            SET    "{1}" = CASE
                     -- Full URL with bucket path: extract key after /bucket/
                     WHEN "{1}" LIKE 'https://%'
                     THEN regexp_replace(
                              split_part("{1}", '?', 1),          -- strip query string
                              '^https?://[^/]+/[^/]+/',           -- strip scheme + host + bucket
                              ''
                          )
                     -- Non-URL value with query string: just strip ?...
                     ELSE split_part("{1}", '?', 1)
                   END
            WHERE  "{1}" LIKE '%?X-Amz-%';
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CreatorProfiles
            migrationBuilder.Sql(string.Format(FixColumnSql, "CreatorProfiles", "ProfileImageUrl"));
            migrationBuilder.Sql(string.Format(FixColumnSql, "CreatorProfiles", "BannerImageUrl"));

            // Creators
            migrationBuilder.Sql(string.Format(FixColumnSql, "Creators", "ProfileImageUrl"));
            migrationBuilder.Sql(string.Format(FixColumnSql, "Creators", "CoverImageUrl"));

            // AspNetUsers
            migrationBuilder.Sql(string.Format(FixColumnSql, "AspNetUsers", "ProfileImageUrl"));
            migrationBuilder.Sql(string.Format(FixColumnSql, "AspNetUsers", "CoverImageUrl"));

            // Tracks (AudioUrl and CoverArtUrl can also have stored presigned URLs)
            migrationBuilder.Sql(string.Format(FixColumnSql, "Tracks", "AudioUrl"));
            migrationBuilder.Sql(string.Format(FixColumnSql, "Tracks", "CoverArtUrl"));

            // Library (denormalized AudioUrl copied from Track)
            migrationBuilder.Sql(string.Format(FixColumnSql, "Library", "AudioUrl"));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-fix migration — not reversible (original presigned URLs are expired anyway).
        }
    }
}
