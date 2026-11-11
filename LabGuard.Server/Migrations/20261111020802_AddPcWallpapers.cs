using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPcWallpapers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WallpaperId",
                table: "Pcs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Wallpapers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallpapers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pcs_WallpaperId",
                table: "Pcs",
                column: "WallpaperId");

            migrationBuilder.CreateIndex(
                name: "IX_Wallpapers_Sha256",
                table: "Wallpapers",
                column: "Sha256",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Pcs_Wallpapers_WallpaperId",
                table: "Pcs",
                column: "WallpaperId",
                principalTable: "Wallpapers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Pcs_Wallpapers_WallpaperId",
                table: "Pcs");

            migrationBuilder.DropTable(
                name: "Wallpapers");

            migrationBuilder.DropIndex(
                name: "IX_Pcs_WallpaperId",
                table: "Pcs");

            migrationBuilder.DropColumn(
                name: "WallpaperId",
                table: "Pcs");
        }
    }
}
