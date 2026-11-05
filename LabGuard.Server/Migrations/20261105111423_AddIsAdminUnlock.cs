using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddIsAdminUnlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdminUnlock",
                table: "PcSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAdminUnlock",
                table: "PcSessions");
        }
    }
}
