using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class JoinRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "refused_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_note",
                table: "users",
                type: "character varying(800)",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "requested_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "site_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    open_sign_up = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_settings", x => x.id);
                    table.CheckConstraint("ck_site_settings_one_row", "id = 1");
                });

            migrationBuilder.InsertData(
                table: "site_settings",
                columns: new[] { "id", "open_sign_up" },
                values: new object[] { 1, false });

            migrationBuilder.CreateIndex(
                name: "ix_users_requested_at",
                table: "users",
                column: "requested_at",
                filter: "requested_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_refused",
                table: "users",
                sql: "refused_at IS NULL OR requested_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_request",
                table: "users",
                sql: "requested_at IS NULL OR NOT is_owner");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Going back: a request to join would otherwise become a member. It is only an account and its sessions, so it goes.
            migrationBuilder.Sql("DELETE FROM sessions WHERE user_id IN (SELECT id FROM users WHERE requested_at IS NOT NULL)");
            migrationBuilder.Sql("DELETE FROM users WHERE requested_at IS NOT NULL");
            migrationBuilder.DropTable(
                name: "site_settings");

            migrationBuilder.DropIndex(
                name: "ix_users_requested_at",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_refused",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_request",
                table: "users");

            migrationBuilder.DropColumn(
                name: "refused_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "request_note",
                table: "users");

            migrationBuilder.DropColumn(
                name: "requested_at",
                table: "users");
        }
    }
}
