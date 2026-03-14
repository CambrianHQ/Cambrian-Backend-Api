using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddObservabilityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "StripeWebhookEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Processed",
                table: "StripeWebhookEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Purchases",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Payload",
                table: "StripeWebhookEvents");

            migrationBuilder.DropColumn(
                name: "Processed",
                table: "StripeWebhookEvents");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Purchases");
        }
    }
}
