using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Security_Camera_Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTrackingToPiDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceType",
                table: "PiDevices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Camera");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "PiDevices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PiDevices_SystemId_DeviceType",
                table: "PiDevices",
                columns: new[] { "SystemId", "DeviceType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PiDevices_SystemId_DeviceType",
                table: "PiDevices");

            migrationBuilder.DropColumn(
                name: "DeviceType",
                table: "PiDevices");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "PiDevices");
        }
    }
}
