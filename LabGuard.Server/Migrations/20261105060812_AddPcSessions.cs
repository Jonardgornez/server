using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "Pcs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LastFailedLoginUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LoginLockedUntilUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PcSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PcId = table.Column<int>(type: "INTEGER", nullable: false),
                    PcName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UserAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DefaultTimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    EndedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PcSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PcSessions_PcId",
                table: "PcSessions",
                column: "PcId",
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PcSessions_UserAccountId",
                table: "PcSessions",
                column: "UserAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PcSessions");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "LastFailedLoginUtc",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "LoginLockedUntilUtc",
                table: "Pcs");
        }
    }
}
