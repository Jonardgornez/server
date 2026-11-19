using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LabGuard.Server.Migrations
{
    /// <inheritdoc />
    public partial class AdminSessionNeverExpires : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "IdleTimeoutMinutes",
                table: "ServerSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            // Existing installs switch to never signing the admin out for being idle, the same as a new install.
            migrationBuilder.Sql("UPDATE \"ServerSettings\" SET \"IdleTimeoutMinutes\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The earlier version can't read 0, so a never setting goes back to the earlier default of 30 minutes.
            migrationBuilder.Sql("UPDATE \"ServerSettings\" SET \"IdleTimeoutMinutes\" = 30 WHERE \"IdleTimeoutMinutes\" = 0;");

            migrationBuilder.AlterColumn<int>(
                name: "IdleTimeoutMinutes",
                table: "ServerSettings",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldDefaultValue: 0);
        }
    }
}
