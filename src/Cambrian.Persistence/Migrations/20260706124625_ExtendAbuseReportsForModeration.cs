using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendAbuseReportsForModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AbuseReports_Tracks_TrackId",
                table: "AbuseReports");

            migrationBuilder.AlterColumn<Guid>(
                name: "TrackId",
                table: "AbuseReports",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "ReportedByUserId",
                table: "AbuseReports",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "AbuseReports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvestigatedAt",
                table: "AbuseReports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvestigatedByUserId",
                table: "AbuseReports",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolutionNote",
                table: "AbuseReports",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetId",
                table: "AbuseReports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                table: "AbuseReports",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "track");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "AbuseReports",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddForeignKey(
                name: "FK_AbuseReports_Tracks_TrackId",
                table: "AbuseReports",
                column: "TrackId",
                principalTable: "Tracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AbuseReports_Tracks_TrackId",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "Details",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "InvestigatedAt",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "InvestigatedByUserId",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "ResolutionNote",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "TargetId",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "TargetType",
                table: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "AbuseReports");

            migrationBuilder.AlterColumn<Guid>(
                name: "TrackId",
                table: "AbuseReports",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ReportedByUserId",
                table: "AbuseReports",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AbuseReports_Tracks_TrackId",
                table: "AbuseReports",
                column: "TrackId",
                principalTable: "Tracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
