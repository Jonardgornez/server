using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPowerCountdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PowerCountdownSeconds",
                table: "ServerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<long>(
                name: "PowerCancelRequestedAtUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PowerCountdownSeconds",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PowerCountdownSeconds",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "PowerCancelRequestedAtUtc",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "PowerCountdownSeconds",
                table: "Pcs");
        }
    }
}
