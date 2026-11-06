using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcShutdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ShutdownDeliveredAtUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ShutdownRequestedAtUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShutdownDeliveredAtUtc",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "ShutdownRequestedAtUtc",
                table: "Pcs");
        }
    }
}
