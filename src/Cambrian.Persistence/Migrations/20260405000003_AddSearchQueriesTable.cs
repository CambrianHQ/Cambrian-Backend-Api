using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchQueriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchQueries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Query = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Filters = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchQueries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchQueries_Query",
                table: "SearchQueries",
                column: "Query");

            migrationBuilder.CreateIndex(
                name: "IX_SearchQueries_CreatedAt",
                table: "SearchQueries",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SearchQueries");
        }
    }
}
