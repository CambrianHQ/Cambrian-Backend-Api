using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchasedReleaseCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreditSource",
                table: "MasteringJobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReleaseCreditPurchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Credits = table.Column<int>(type: "integer", nullable: false),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Pack = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "paid"),
                    StripeSessionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseCreditPurchases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_release_credit_purchases_creator",
                table: "ReleaseCreditPurchases",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "ux_release_credit_purchases_session",
                table: "ReleaseCreditPurchases",
                column: "StripeSessionId",
                unique: true,
                filter: "\"StripeSessionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseCreditPurchases");

            migrationBuilder.DropColumn(
                name: "CreditSource",
                table: "MasteringJobs");
        }
    }
}
