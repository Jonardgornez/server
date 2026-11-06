using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddServerSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultApprovalMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DefaultApprovalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    NoticeEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoticeText = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultViewMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerSettings", x => x.Id);
                    table.CheckConstraint("CK_ServerSettings_SingleRow", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerSettings");
        }
    }
}
