using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <summary>
    /// Backfills default tier prices for any Track where all three license tiers
    /// were stored as 0 cents — a side-effect of uploads predating the price
    /// validation added in 71f167f. Tracks with zero prices render as "Coming
    /// soon" on the marketplace and 404 the License Track flow, so we set the
    /// floor of each published tier (per /pricing page) to make them sellable.
    ///
    /// Defaults (cents): Personal=999, Commercial=4999, Extended=19999.
    /// Only touches rows where ALL THREE prices were 0 — does not overwrite
    /// any creator's deliberate pricing.
    /// </summary>
    public partial class BackfillZeroTrackPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Tracks""
                SET
                    ""NonExclusivePriceCents""    = 999,
                    ""ExclusivePriceCents""       = 4999,
                    ""CopyrightBuyoutPriceCents"" = 19999,
                    ""Price""                     = 9.99
                WHERE ""NonExclusivePriceCents""    = 0
                  AND ""ExclusivePriceCents""       = 0
                  AND ""CopyrightBuyoutPriceCents"" = 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op. Reversing this would require knowing which
            // rows we touched; we'd risk zeroing out prices that creators have
            // since edited.
        }
    }
}
