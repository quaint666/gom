using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class ConversationMenu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cleared_seq",
                table: "conversation_members",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "hidden_seq",
                table: "conversation_members",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "marked_unread",
                table: "conversation_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pinned_at",
                table: "conversation_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_members_cleared",
                table: "conversation_members",
                sql: "cleared_seq >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_members_cleared",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "cleared_seq",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "hidden_seq",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "marked_unread",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "pinned_at",
                table: "conversation_members");
        }
    }
}
