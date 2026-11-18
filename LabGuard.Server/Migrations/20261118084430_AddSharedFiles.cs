using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharedFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PcFileTransfers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PcId = table.Column<int>(type: "INTEGER", nullable: false),
                    PcName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SharedFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Folder = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Subfolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SavedAs = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PcFileTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PcFileTransfers_Pcs_PcId",
                        column: x => x.PcId,
                        principalTable: "Pcs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PcFileTransfers_SharedFiles_SharedFileId",
                        column: x => x.SharedFileId,
                        principalTable: "SharedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PcFileTransfers_CreatedUtc",
                table: "PcFileTransfers",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PcFileTransfers_PcId_Status",
                table: "PcFileTransfers",
                columns: new[] { "PcId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PcFileTransfers_SharedFileId",
                table: "PcFileTransfers",
                column: "SharedFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedFiles_Sha256",
                table: "SharedFiles",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PcFileTransfers");

            migrationBuilder.DropTable(
                name: "SharedFiles");
        }
    }
}
