using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class ConversationLook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.AddColumn<string>(
                name: "palette",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pattern",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6, 7)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The lines saying who changed a conversation's look have no kind before this: they go, with their events and hides
            // (a notice cannot be pinned, reacted to or carry files).
            migrationBuilder.Sql("""
                DELETE FROM message_events WHERE message_id IN (SELECT id FROM messages WHERE notice = 7);
                DELETE FROM message_hides WHERE message_id IN (SELECT id FROM messages WHERE notice = 7);
                DELETE FROM messages WHERE notice = 7;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "palette",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "pattern",
                table: "conversations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6)");
        }
    }
}
