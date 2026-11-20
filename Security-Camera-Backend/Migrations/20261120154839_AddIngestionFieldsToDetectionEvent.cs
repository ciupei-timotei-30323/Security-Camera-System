using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Security_Camera_Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestionFieldsToDetectionEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsImageSynced",
                table: "DetectionEvents",
                newName: "IsOfflineSync");

            migrationBuilder.AlterColumn<bool>(
                name: "IsSuspect",
                table: "DetectionEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEmailSent",
                table: "DetectionEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEmailSent",
                table: "DetectionEvents");

            migrationBuilder.RenameColumn(
                name: "IsOfflineSync",
                table: "DetectionEvents",
                newName: "IsImageSynced");

            migrationBuilder.AlterColumn<bool>(
                name: "IsSuspect",
                table: "DetectionEvents",
                type: "boolean",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "boolean");
        }
    }
}
