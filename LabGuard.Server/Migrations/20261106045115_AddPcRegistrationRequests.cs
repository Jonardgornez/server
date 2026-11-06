using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcRegistrationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PcRegistrationRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Nonce = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PairingNumber = table.Column<string>(type: "TEXT", maxLength: 6, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PcRegistrationRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PcRegistrationRequests_Status",
                table: "PcRegistrationRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PcRegistrationRequests_TokenHash",
                table: "PcRegistrationRequests",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PcRegistrationRequests");
        }
    }
}
