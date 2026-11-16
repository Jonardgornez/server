using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUninstallPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UninstallPasswordHash",
                table: "ServerSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UninstallPasswordIterations",
                table: "ServerSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UninstallPasswordSalt",
                table: "ServerSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UninstallPasswordHash",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "UninstallPasswordIterations",
                table: "ServerSettings");

            migrationBuilder.DropColumn(
                name: "UninstallPasswordSalt",
                table: "ServerSettings");
        }
    }
}
