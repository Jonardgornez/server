using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcInternetBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InternetBlockApplied",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InternetBlocked",
                table: "Pcs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InternetBlockApplied",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "InternetBlocked",
                table: "Pcs");
        }
    }
}
