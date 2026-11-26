using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Calls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.CreateTable(
                name: "calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    callee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caller_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answer_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    caller_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    callee_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calls", x => x.id);
                    table.CheckConstraint("ck_calls_outcome", "(state = 3) = (outcome IS NOT NULL) AND (outcome IS NULL OR outcome IN (1, 2, 3, 4, 5))");
                    table.CheckConstraint("ck_calls_state", "state IN (1, 2, 3)");
                    table.CheckConstraint("ck_calls_two", "caller_id <> callee_id");
                    table.ForeignKey(
                        name: "fk_calls_callee_id",
                        column: x => x.callee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calls_caller_id",
                        column: x => x.caller_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calls_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_calls_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "call_parties",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    call_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_call_parties", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_call_parties_call_id",
                        column: x => x.call_id,
                        principalTable: "calls",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_call_parties_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6, 7, 8)");

            migrationBuilder.CreateIndex(
                name: "ix_call_parties_call_id",
                table: "call_parties",
                column: "call_id");

            migrationBuilder.CreateIndex(
                name: "ix_calls_callee_id",
                table: "calls",
                column: "callee_id");

            migrationBuilder.CreateIndex(
                name: "ix_calls_caller_id_client_id",
                table: "calls",
                columns: new[] { "caller_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calls_conversation_id",
                table: "calls",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_calls_message_id",
                table: "calls",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_calls_state",
                table: "calls",
                column: "state");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "call_parties");

            migrationBuilder.DropTable(
                name: "calls");

            // The calls' lines in the chat cannot stay under the old rule: they go, with what hangs off them.
            migrationBuilder.Sql("""
                DELETE FROM message_hides WHERE message_id IN (SELECT id FROM messages WHERE notice = 8);
                DELETE FROM message_events WHERE message_id IN (SELECT id FROM messages WHERE notice = 8);
                DELETE FROM messages WHERE notice = 8;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6, 7)");
        }
    }
}
