using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Replies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reply_to_id",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_reply_to_id",
                table: "messages",
                column: "reply_to_id");

            migrationBuilder.AddForeignKey(
                name: "fk_messages_reply_to_id",
                table: "messages",
                column: "reply_to_id",
                principalTable: "messages",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_messages_reply_to_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_reply_to_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "reply_to_id",
                table: "messages");
        }
    }
}
