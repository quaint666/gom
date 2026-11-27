using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Disappearing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "disappear_seconds",
                table: "conversations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_expires_at",
                table: "messages",
                column: "expires_at",
                filter: "expires_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_expires_at",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "disappear_seconds",
                table: "conversations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6, 7, 8)");
        }
    }
}
