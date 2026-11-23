using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Accounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gom_id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    is_owner = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_gom_id", "gom_id ~ '^[a-z0-9_]{3,20}$'");
                });

            migrationBuilder.CreateTable(
                name: "one_time_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    target_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_one_time_links", x => x.id);
                    table.CheckConstraint("ck_links_kind", "kind IN ('invite', 'reset')");
                    table.CheckConstraint("ck_links_target", "(kind = 'reset') = (target_user_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_one_time_links_consumed_by",
                        column: x => x.consumed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_one_time_links_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_one_time_links_target_user_id",
                        column: x => x.target_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    device_label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_sessions_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_one_time_links_consumed_by",
                table: "one_time_links",
                column: "consumed_by");

            migrationBuilder.CreateIndex(
                name: "ix_one_time_links_created_by",
                table: "one_time_links",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_one_time_links_target_user_id",
                table: "one_time_links",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_one_time_links_token_hash",
                table: "one_time_links",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_token_hash",
                table: "sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_user_id",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_gom_id",
                table: "users",
                column: "gom_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_is_owner",
                table: "users",
                column: "is_owner",
                unique: true,
                filter: "is_owner");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "one_time_links");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
