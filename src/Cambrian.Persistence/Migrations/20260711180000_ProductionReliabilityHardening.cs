using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cambrian.Persistence.Migrations;

[DbContext(typeof(CambrianDbContext))]
[Migration("20260711180000_ProductionReliabilityHardening")]
public sealed class ProductionReliabilityHardening : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("RequestHash", "ApiIdempotencyKeys", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("Status", "ApiIdempotencyKeys", type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "completed");
        migrationBuilder.AddColumn<string>("Genres", "CreatorProfiles", type: "character varying(1000)", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<string>("StripePaymentIntentId", "ReleaseCreditPurchases", type: "character varying(255)", maxLength: 255, nullable: true);

        migrationBuilder.AddColumn<DateTime>("DeletedAt", "Tracks", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("DeletedByUserId", "Tracks", type: "character varying(450)", maxLength: 450, nullable: true);
        migrationBuilder.AddColumn<string>("PreDeleteStatus", "Tracks", type: "character varying(30)", maxLength: 30, nullable: true);
        migrationBuilder.AddColumn<string>("PreDeleteVisibility", "Tracks", type: "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<DateTime>("PurgeRequestedAt", "Tracks", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>("PurgedAt", "Tracks", type: "timestamp with time zone", nullable: true);

        migrationBuilder.CreateIndex("ix_release_credit_purchases_payment_intent", "ReleaseCreditPurchases", "StripePaymentIntentId", filter: "\"StripePaymentIntentId\" IS NOT NULL");
        migrationBuilder.CreateIndex("IX_Tracks_DeletedAt", "Tracks", "DeletedAt");
        migrationBuilder.CreateIndex("IX_Tracks_PurgeRequestedAt", "Tracks", "PurgeRequestedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("ix_release_credit_purchases_payment_intent", "ReleaseCreditPurchases");
        migrationBuilder.DropIndex("IX_Tracks_DeletedAt", "Tracks");
        migrationBuilder.DropIndex("IX_Tracks_PurgeRequestedAt", "Tracks");
        migrationBuilder.DropColumn("RequestHash", "ApiIdempotencyKeys");
        migrationBuilder.DropColumn("Status", "ApiIdempotencyKeys");
        migrationBuilder.DropColumn("Genres", "CreatorProfiles");
        migrationBuilder.DropColumn("StripePaymentIntentId", "ReleaseCreditPurchases");
        migrationBuilder.DropColumn("DeletedAt", "Tracks");
        migrationBuilder.DropColumn("DeletedByUserId", "Tracks");
        migrationBuilder.DropColumn("PreDeleteStatus", "Tracks");
        migrationBuilder.DropColumn("PreDeleteVisibility", "Tracks");
        migrationBuilder.DropColumn("PurgeRequestedAt", "Tracks");
        migrationBuilder.DropColumn("PurgedAt", "Tracks");
    }
}
