using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncOpportunitiesTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncBriefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuyerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Genre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Mood = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Budget = table.Column<decimal>(type: "numeric", nullable: false),
                    Deadline = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsageType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Territory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "open"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncBriefs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncBriefs_AspNetUsers_BuyerUserId",
                        column: x => x.BuyerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncBriefs_BuyerUserId",
                table: "SyncBriefs",
                column: "BuyerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncBriefs_Status",
                table: "SyncBriefs",
                column: "Status");

            migrationBuilder.CreateTable(
                name: "SyncSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncBriefId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    TrackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncSubmissions_SyncBriefs_SyncBriefId",
                        column: x => x.SyncBriefId,
                        principalTable: "SyncBriefs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncSubmissions_AspNetUsers_CreatorUserId",
                        column: x => x.CreatorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncSubmissions_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncSubmissions_SyncBriefId",
                table: "SyncSubmissions",
                column: "SyncBriefId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncSubmissions_CreatorUserId",
                table: "SyncSubmissions",
                column: "CreatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncSubmissions_TrackId",
                table: "SyncSubmissions",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SyncSubmissions");
            migrationBuilder.DropTable(name: "SyncBriefs");
        }
    }
}
