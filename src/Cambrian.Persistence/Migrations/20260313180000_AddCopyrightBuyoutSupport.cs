using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCopyrightBuyoutSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Track: add Status column
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Tracks",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "available");

            // Track: add CopyrightOwnerId column
            migrationBuilder.AddColumn<string>(
                name: "CopyrightOwnerId",
                table: "Tracks",
                type: "text",
                nullable: true);

            // Track: add CopyrightTransferredAt column
            migrationBuilder.AddColumn<DateTime>(
                name: "CopyrightTransferredAt",
                table: "Tracks",
                type: "timestamp with time zone",
                nullable: true);

            // Track: add OriginalCreatorId column
            migrationBuilder.AddColumn<string>(
                name: "OriginalCreatorId",
                table: "Tracks",
                type: "text",
                nullable: true);

            // LicenseCertificate: add CopyrightOwner column
            migrationBuilder.AddColumn<string>(
                name: "CopyrightOwner",
                table: "LicenseCertificates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Backfill existing tracks: set Status based on ExclusiveSold flag
            migrationBuilder.Sql(
                "UPDATE \"Tracks\" SET \"Status\" = 'exclusive_sold' WHERE \"ExclusiveSold\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CopyrightOwner", table: "LicenseCertificates");
            migrationBuilder.DropColumn(name: "OriginalCreatorId", table: "Tracks");
            migrationBuilder.DropColumn(name: "CopyrightTransferredAt", table: "Tracks");
            migrationBuilder.DropColumn(name: "CopyrightOwnerId", table: "Tracks");
            migrationBuilder.DropColumn(name: "Status", table: "Tracks");
        }
    }
}
