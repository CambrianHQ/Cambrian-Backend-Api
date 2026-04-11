using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations;

public partial class AddTrackTaxonomyFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PrimaryGenre",
            table: "Tracks",
            type: "character varying(60)",
            maxLength: 60,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Subgenre",
            table: "Tracks",
            type: "character varying(60)",
            maxLength: 60,
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE "Tracks"
            SET "Subgenre" = "Genre"
            WHERE "Genre" IS NOT NULL AND "Subgenre" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PrimaryGenre",
            table: "Tracks");

        migrationBuilder.DropColumn(
            name: "Subgenre",
            table: "Tracks");
    }
}
