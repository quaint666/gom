using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Messages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_message_at",
                table: "conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.CheckConstraint("ck_messages_body", "deleted_at IS NOT NULL OR length(body) > 0");
                    table.CheckConstraint("ck_messages_seq", "seq > 0");
                    table.ForeignKey(
                        name: "fk_messages_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_messages_sender_id",
                        column: x => x.sender_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_events",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_events", x => new { x.conversation_id, x.seq });
                    table.CheckConstraint("ck_message_events_kind", "kind IN ('created', 'deleted')");
                    table.CheckConstraint("ck_message_events_seq", "seq > 0");
                    table.ForeignKey(
                        name: "fk_message_events_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_message_events_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_message_events_message_id",
                table: "message_events",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_id_seq",
                table: "messages",
                columns: new[] { "conversation_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_sender_id_client_id",
                table: "messages",
                columns: new[] { "sender_id", "client_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_events");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropColumn(
                name: "last_message_at",
                table: "conversations");
        }
    }
}
