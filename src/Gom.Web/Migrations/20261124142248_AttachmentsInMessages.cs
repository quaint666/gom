using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class AttachmentsInMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_body",
                table: "messages");

            migrationBuilder.AddColumn<int>(
                name: "position",
                table: "attachments",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "position",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_body",
                table: "messages",
                sql: "deleted_at IS NOT NULL OR length(body) > 0");
        }
    }
}
