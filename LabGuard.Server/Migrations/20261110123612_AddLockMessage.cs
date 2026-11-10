using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLockMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LockMessage",
                table: "ServerSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "This computer has been locked by the administrator.");

            migrationBuilder.AddColumn<string>(
                name: "LockMessage",
                table: "Pcs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockMessage",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "LockMessage",
                table: "Pcs");
        }
    }
}
