using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenBackgrounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScreenBackgrounds",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenBackgrounds", x => x.Kind);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScreenBackgrounds");
        }
    }
}
