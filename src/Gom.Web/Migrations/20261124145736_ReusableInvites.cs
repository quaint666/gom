using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class ReusableInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_uses",
                table: "one_time_links",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "uses",
                table: "one_time_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "invite_uses",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invite_uses", x => new { x.link_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_invite_uses_link_id",
                        column: x => x.link_id,
                        principalTable: "one_time_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invite_uses_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Invites made before this change were for one person: keep them so, and keep who used them.
            migrationBuilder.Sql("UPDATE one_time_links SET max_uses = 1, uses = CASE WHEN consumed_at IS NOT NULL THEN 1 ELSE 0 END WHERE kind = 'invite'");
            migrationBuilder.Sql("INSERT INTO invite_uses (link_id, user_id, used_at) SELECT id, consumed_by, consumed_at FROM one_time_links WHERE kind = 'invite' AND consumed_by IS NOT NULL AND consumed_at IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_links_uses",
                table: "one_time_links",
                sql: "uses >= 0 AND (max_uses IS NULL OR (max_uses >= 1 AND uses <= max_uses))");

            migrationBuilder.CreateIndex(
                name: "ix_invite_uses_user_id",
                table: "invite_uses",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invite_uses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_uses",
                table: "one_time_links");

            migrationBuilder.DropColumn(
                name: "max_uses",
                table: "one_time_links");

            migrationBuilder.DropColumn(
                name: "uses",
                table: "one_time_links");
        }
    }
}
