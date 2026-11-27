using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class TwoStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "two_step_key",
                table: "users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "two_step_last_step",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "two_step_key",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_step_last_step",
                table: "users");
        }
    }
}
