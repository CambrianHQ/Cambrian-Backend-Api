using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "Payouts");

            migrationBuilder.AddColumn<int>(
                name: "AmountCents",
                table: "Payouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StripeAccountId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountCents",
                table: "Payouts");

            migrationBuilder.DropColumn(
                name: "StripeAccountId",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<double>(
                name: "Amount",
                table: "Payouts",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
