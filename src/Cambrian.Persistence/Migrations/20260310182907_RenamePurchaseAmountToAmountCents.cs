using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenamePurchaseAmountToAmountCents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the new int column first
            migrationBuilder.AddColumn<int>(
                name: "AmountCents",
                table: "Purchases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Copy existing data: Amount (dollars as double) → AmountCents (cents as int)
            migrationBuilder.Sql(
                "UPDATE \"Purchases\" SET \"AmountCents\" = CAST(ROUND(\"Amount\" * 100) AS integer) WHERE \"Amount\" IS NOT NULL;");

            // Drop the old column
            migrationBuilder.DropColumn(
                name: "Amount",
                table: "Purchases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountCents",
                table: "Purchases");

            migrationBuilder.AddColumn<double>(
                name: "Amount",
                table: "Purchases",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }
    }
}
