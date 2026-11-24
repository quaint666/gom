using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class JoinApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "join_approval",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "group_bans",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    banned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_bans", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_group_bans_banned_by",
                        column: x => x.banned_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_group_bans_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_group_bans_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "join_requests",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_join_requests", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_join_requests_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_join_requests_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_join_requests_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_bans_banned_by",
                table: "group_bans",
                column: "banned_by");

            migrationBuilder.CreateIndex(
                name: "ix_group_bans_user_id",
                table: "group_bans",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_join_requests_requested_by",
                table: "join_requests",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_join_requests_user_id",
                table: "join_requests",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_bans");

            migrationBuilder.DropTable(
                name: "join_requests");

            migrationBuilder.DropColumn(
                name: "join_approval",
                table: "conversations");
        }
    }
}
