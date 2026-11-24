using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class MessageReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_message_events_kind",
                table: "message_events");

            migrationBuilder.CreateTable(
                name: "message_reactions",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    emoji = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_reactions", x => new { x.message_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_message_reactions_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_message_reactions_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_events_kind",
                table: "message_events",
                sql: "kind IN ('created', 'deleted', 'updated')");

            migrationBuilder.CreateIndex(
                name: "ix_message_reactions_user_id",
                table: "message_reactions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_reactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_message_events_kind",
                table: "message_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_message_events_kind",
                table: "message_events",
                sql: "kind IN ('created', 'deleted')");
        }
    }
}
