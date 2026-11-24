using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class ReadReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "share_read_receipts",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "share_read_receipts",
                table: "users");
        }
    }
}
