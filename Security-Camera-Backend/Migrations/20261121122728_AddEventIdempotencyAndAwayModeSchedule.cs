using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Security_Camera_Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddEventIdempotencyAndAwayModeSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnnotatedImageUri",
                table: "DetectionEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientEventId",
                table: "DetectionEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AlertEmail",
                table: "AwayModeSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "EndTime",
                table: "AwayModeSettings",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "AwayModeSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "StartTime",
                table: "AwayModeSettings",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "AwayModeSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Europe/Bucharest");

            // Preserve the historic arm/disarm state for existing rows.
            migrationBuilder.Sql("""
                UPDATE "AwayModeSettings"
                SET "Mode" = CASE WHEN "IsArmed" THEN 1 ELSE 0 END,
                    "TimeZone" = 'Europe/Bucharest';
                """);

            // Image columns now store bare file names served through the tenant-scoped
            // /api/events/{id}/face and /annotated endpoints, not public /captures URLs.
            migrationBuilder.Sql("""
                UPDATE "DetectionEvents"
                SET "FullImageUri" = substring("FullImageUri" from '[^/]+$')
                WHERE "FullImageUri" IS NOT NULL;

                UPDATE "DetectionEvents"
                SET "DetectedFaceUri" = substring("DetectedFaceUri" from '[^/]+$')
                WHERE "DetectedFaceUri" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DetectionEvents_SystemId_ClientEventId",
                table: "DetectionEvents",
                columns: new[] { "SystemId", "ClientEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DetectionEvents_SystemId_ClientEventId",
                table: "DetectionEvents");

            migrationBuilder.DropColumn(
                name: "AnnotatedImageUri",
                table: "DetectionEvents");

            migrationBuilder.DropColumn(
                name: "ClientEventId",
                table: "DetectionEvents");

            migrationBuilder.DropColumn(
                name: "AlertEmail",
                table: "AwayModeSettings");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "AwayModeSettings");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "AwayModeSettings");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "AwayModeSettings");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "AwayModeSettings");
        }
    }
}
