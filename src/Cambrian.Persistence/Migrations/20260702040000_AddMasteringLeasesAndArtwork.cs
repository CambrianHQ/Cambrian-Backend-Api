using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    // RELEASE-GATE FIX: EF Core only discovers migrations that carry BOTH
    // [DbContext] and [Migration] (MigrationsAssembly filters on the context
    // type). This hand-written migration shipped without [DbContext], so
    // `dotnet ef database update` silently skipped it — tests still passed
    // because the fixture builds schema from the MODEL, while a prod deploy
    // would crash on the missing MasteringJobs lease/artwork columns.
    [DbContext(typeof(CambrianDbContext))]
    [Migration("20260702040000_AddMasteringLeasesAndArtwork")]
    public partial class AddMasteringLeasesAndArtwork : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverArtKey",
                table: "MasteringJobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "MasteringJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingLeaseExpiresAt",
                table: "MasteringJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProcessingLeaseId",
                table: "MasteringJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProcessingStartedAt",
                table: "MasteringJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_Creator_Kind_ContentHash",
                table: "MasteringJobs",
                columns: new[] { "CreatorId", "Kind", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_MasteringJobs_Status_ProcessingLeaseExpiresAt",
                table: "MasteringJobs",
                columns: new[] { "Status", "ProcessingLeaseExpiresAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MasteringJobs_Creator_Kind_ContentHash",
                table: "MasteringJobs");

            migrationBuilder.DropIndex(
                name: "IX_MasteringJobs_Status_ProcessingLeaseExpiresAt",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "CoverArtKey",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "ProcessingLeaseExpiresAt",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "ProcessingLeaseId",
                table: "MasteringJobs");

            migrationBuilder.DropColumn(
                name: "ProcessingStartedAt",
                table: "MasteringJobs");
        }
    }
}
