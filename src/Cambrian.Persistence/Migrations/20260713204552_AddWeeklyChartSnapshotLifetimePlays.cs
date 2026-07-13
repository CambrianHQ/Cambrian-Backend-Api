using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyChartSnapshotLifetimePlays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LifetimePlays",
                table: "WeeklyChartSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LifetimePlays",
                table: "WeeklyChartSnapshots");
        }
    }
}
