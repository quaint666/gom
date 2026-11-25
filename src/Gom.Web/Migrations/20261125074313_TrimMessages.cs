using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class TrimMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_messages_reply_to_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_reply_to_id",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "ix_messages_reply_to_id",
                table: "messages",
                column: "reply_to_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5)");

            migrationBuilder.AddForeignKey(
                name: "fk_messages_reply_to_id",
                table: "messages",
                column: "reply_to_id",
                principalTable: "messages",
                principalColumn: "id");
        }
    }
}
