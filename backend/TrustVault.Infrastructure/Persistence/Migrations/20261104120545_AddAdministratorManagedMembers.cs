using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TrustVault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministratorManagedMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimaryAdministrator",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_IsPrimaryAdministrator",
                table: "AspNetUsers",
                column: "IsPrimaryAdministrator",
                unique: true,
                filter: "\"IsPrimaryAdministrator\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_IsPrimaryAdministrator",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsPrimaryAdministrator",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "AspNetUsers");
        }
    }
}
