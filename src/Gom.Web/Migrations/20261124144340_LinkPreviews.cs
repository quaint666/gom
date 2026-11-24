using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class LinkPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_kind",
                table: "attachments");

            migrationBuilder.CreateTable(
                name: "message_previews",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    description = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    site = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_previews", x => x.message_id);
                    table.ForeignKey(
                        name: "fk_message_previews_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_kind",
                table: "attachments",
                sql: "kind in (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_previews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_attachments_kind",
                table: "attachments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_attachments_kind",
                table: "attachments",
                sql: "kind in (0, 1)");
        }
    }
}
