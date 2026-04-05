using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoundingCreatorColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFoundingCreator",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "FoundingCreatorEnrolledAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FoundingCreatorExpiresAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFoundingCreator",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FoundingCreatorEnrolledAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FoundingCreatorExpiresAt",
                table: "AspNetUsers");
        }
    }
}
