using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcLockAndPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PausedAtUtc",
                table: "PcSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LockedAtUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PausedAtUtc",
                table: "PcSessions");

            migrationBuilder.DropColumn(
                name: "LockedAtUtc",
                table: "Pcs");
        }
    }
}
