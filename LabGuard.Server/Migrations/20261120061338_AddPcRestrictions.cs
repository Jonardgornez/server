using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisabledDrives",
                table: "Pcs",
                type: "TEXT",
                maxLength: 26,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ExplorerRestartGeneration",
                table: "Pcs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HiddenDrives",
                table: "Pcs",
                type: "TEXT",
                maxLength: 26,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RestrictedPolicies",
                table: "Pcs",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisabledDrives",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "ExplorerRestartGeneration",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "HiddenDrives",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "RestrictedPolicies",
                table: "Pcs");
        }
    }
}
