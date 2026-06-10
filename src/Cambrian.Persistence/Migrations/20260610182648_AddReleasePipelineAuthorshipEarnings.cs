using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleasePipelineAuthorshipEarnings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "MasteringJobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "MasteringJobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "mastering");

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "MasteringJobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageHistoryJson",
                table: "MasteringJobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FanSubscriptionPriceCents",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuthorshipRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ArtistName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: true),
                    RecordHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CanonicalRecordJson = table.Column<string>(type: "text", nullable: true),
                    Signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SignatureAlgorithm = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    KeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StripeSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorshipRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EarningsTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtistUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrossCents = table.Column<long>(type: "bigint", nullable: false),
                    FeeCents = table.Column<long>(type: "bigint", nullable: false),
                    NetCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ExternalRef = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PayerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EarningsTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FanSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FanUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ArtistUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    PriceCents = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StripeSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StripeSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FanSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_TrackId_ContentHash",
                table: "MasteringJobs",
                columns: new[] { "TrackId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorshipRecords_CreatorId",
                table: "AuthorshipRecords",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorshipRecords_StripeSessionId",
                table: "AuthorshipRecords",
                column: "StripeSessionId",
                unique: true,
                filter: "\"StripeSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorshipRecords_TrackId",
                table: "AuthorshipRecords",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_EarningsTransactions_Artist_CreatedAt",
                table: "EarningsTransactions",
                columns: new[] { "ArtistUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EarningsTransactions_Artist_Source",
                table: "EarningsTransactions",
                columns: new[] { "ArtistUserId", "Source" });

            migrationBuilder.CreateIndex(
                name: "IX_EarningsTransactions_Source_ExternalRef",
                table: "EarningsTransactions",
                columns: new[] { "Source", "ExternalRef" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FanSubscriptions_ArtistUserId",
                table: "FanSubscriptions",
                column: "ArtistUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FanSubscriptions_FanUserId",
                table: "FanSubscriptions",
                column: "FanUserId");

            migrationBuilder.CreateIndex(
                name: "IX_FanSubscriptions_StripeSessionId",
                table: "FanSubscriptions",
                column: "StripeSessionId",
                unique: true,
                filter: "\"StripeSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FanSubscriptions_StripeSubscriptionId",
                table: "FanSubscriptions",
                column: "StripeSubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorshipRecords");

            migrationBuilder.DropTable(
                name: "EarningsTransactions");

            migrationBuilder.DropTable(
                name: "FanSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_MasteringJobs_TrackId_ContentHash",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "StageHistoryJson",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "FanSubscriptionPriceCents",
                table: "AspNetUsers");
        }
    }
}
