using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddClientUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientUpdateError",
                table: "Pcs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUpdateState",
                table: "Pcs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ClientUpdateStateAtUtc",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientUpdateTargetVersion",
                table: "Pcs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientVersion",
                table: "Pcs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientRelease",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    UploadedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientRelease", x => x.Id);
                    table.CheckConstraint("CK_ClientRelease_SingleRow", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientRelease");

            migrationBuilder.DropColumn(
                name: "ClientUpdateError",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "ClientUpdateState",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "ClientUpdateStateAtUtc",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "ClientUpdateTargetVersion",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "ClientVersion",
                table: "Pcs");
        }
    }
}
