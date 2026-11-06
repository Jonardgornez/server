using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationCommitments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PairingNumber",
                table: "PcRegistrationRequests",
                type: "TEXT",
                maxLength: 6,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 6);

            migrationBuilder.AlterColumn<string>(
                name: "Nonce",
                table: "PcRegistrationRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "Commitment",
                table: "PcRegistrationRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ServerNonce",
                table: "PcRegistrationRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commitment",
                table: "PcRegistrationRequests");

            migrationBuilder.DropColumn(
                name: "ServerNonce",
                table: "PcRegistrationRequests");

            migrationBuilder.AlterColumn<string>(
                name: "PairingNumber",
                table: "PcRegistrationRequests",
                type: "TEXT",
                maxLength: 6,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 6,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Nonce",
                table: "PcRegistrationRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
