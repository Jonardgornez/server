using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenControlEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScreenControlEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AdminUserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PcName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScreenControlEvents", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScreenControlEvents");
        }
    }
}
