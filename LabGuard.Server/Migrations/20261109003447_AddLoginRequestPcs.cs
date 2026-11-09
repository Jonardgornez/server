using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginRequestPcs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoginRequestId",
                table: "PcSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PcId",
                table: "LoginRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "LoginRequests",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginRequests_PcId_Pending",
                table: "LoginRequests",
                column: "PcId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_LoginRequests_PcId_RequestedAtUtc",
                table: "LoginRequests",
                columns: new[] { "PcId", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LoginRequests_PcId_Pending",
                table: "LoginRequests");

            migrationBuilder.DropIndex(
                name: "IX_LoginRequests_PcId_RequestedAtUtc",
                table: "LoginRequests");

            migrationBuilder.DropColumn(
                name: "LoginRequestId",
                table: "PcSessions");

            migrationBuilder.DropColumn(
                name: "PcId",
                table: "LoginRequests");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "LoginRequests");
        }
    }
}
