using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferrerId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ReferredUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ReferralCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReferredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvertedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CommissionRate = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0.10m),
                    TotalCommissionEarned = table.Column<decimal>(type: "numeric", nullable: false, defaultValue: 0m)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Referrals_AspNetUsers_ReferrerId",
                        column: x => x.ReferrerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferralCode",
                table: "Referrals",
                column: "ReferralCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerId",
                table: "Referrals",
                column: "ReferrerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Referrals");
        }
    }
}
