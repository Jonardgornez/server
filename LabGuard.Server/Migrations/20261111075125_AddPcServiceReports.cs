using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcServiceReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PcServiceReports",
                columns: table => new
                {
                    PcId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceivedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ServiceVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ClientRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClientVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UserSignedIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdateVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UpdateState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    UpdateError = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EventsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PcServiceReports", x => x.PcId);
                    table.ForeignKey(
                        name: "FK_PcServiceReports_Pcs_PcId",
                        column: x => x.PcId,
                        principalTable: "Pcs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PcServiceReports");
        }
    }
}
