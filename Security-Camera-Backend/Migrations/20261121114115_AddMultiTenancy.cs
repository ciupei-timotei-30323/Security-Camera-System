using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Security_Camera_Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");

            migrationBuilder.AddColumn<string>(
                name: "SystemId",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "sys-001");

            migrationBuilder.AddColumn<string>(
                name: "SystemId",
                table: "DetectionEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "sys-001");

            migrationBuilder.CreateTable(
                name: "AwayModeSettings",
                columns: table => new
                {
                    SystemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsArmed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "timezone('utc', now())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AwayModeSettings", x => x.SystemId);
                });

            migrationBuilder.CreateTable(
                name: "CameraSystems",
                columns: table => new
                {
                    SystemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "timezone('utc', now())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraSystems", x => x.SystemId);
                });

            migrationBuilder.CreateTable(
                name: "PiDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SystemId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "timezone('utc', now())")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PiDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PiDevices_CameraSystems_SystemId",
                        column: x => x.SystemId,
                        principalTable: "CameraSystems",
                        principalColumn: "SystemId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_SystemId_Username",
                table: "Users",
                columns: new[] { "SystemId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DetectionEvents_SystemId_Timestamp",
                table: "DetectionEvents",
                columns: new[] { "SystemId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_PiDevices_ApiKeyHash",
                table: "PiDevices",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PiDevices_SystemId",
                table: "PiDevices",
                column: "SystemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AwayModeSettings");

            migrationBuilder.DropTable(
                name: "PiDevices");

            migrationBuilder.DropTable(
                name: "CameraSystems");

            migrationBuilder.DropIndex(
                name: "IX_Users_SystemId_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_DetectionEvents_SystemId_Timestamp",
                table: "DetectionEvents");

            migrationBuilder.DropColumn(
                name: "SystemId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SystemId",
                table: "DetectionEvents");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }
    }
}
