using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class TrimFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                table: "attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4, 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_notice",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "removed_at",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_notice",
                table: "messages",
                sql: "notice IS NULL OR notice IN (1, 2, 3, 4)");
        }
    }
}
