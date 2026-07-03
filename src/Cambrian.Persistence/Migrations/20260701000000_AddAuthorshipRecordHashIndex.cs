using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    public partial class AddAuthorshipRecordHashIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuthorshipRecords_RecordHash",
                table: "AuthorshipRecords",
                column: "RecordHash",
                filter: "\"RecordHash\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuthorshipRecords_RecordHash",
                table: "AuthorshipRecords");
        }
    }
}
