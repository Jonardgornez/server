using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PcMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PcId = table.Column<int>(type: "INTEGER", nullable: false),
                    PcName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AdminUserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    HandedOverUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DeliveredUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PcMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PcMessages_PcId_CreatedUtc",
                table: "PcMessages",
                columns: new[] { "PcId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PcMessages");
        }
    }
}
