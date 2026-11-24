using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Notices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "notice",
                table: "messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "notice_target_id",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_notice_target_id",
                table: "messages",
                column: "notice_target_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice_target",
                table: "messages",
                sql: "(notice IS NOT NULL AND notice IN (1, 2)) = (notice_target_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_messages_notice_target_id",
                table: "messages",
                column: "notice_target_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_messages_notice_target_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_notice_target_id",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice_target",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "notice",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "notice_target_id",
                table: "messages");
        }
    }
}
