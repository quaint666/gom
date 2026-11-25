using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLinkPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The pictures of link previews (kind 2) are not attachments of any message any more. Previews were off everywhere, so
            // none are expected; this makes sure a stray row cannot turn into a file card. A picture file on disk, if there were one, is not removed here.
            migrationBuilder.Sql("DELETE FROM attachments WHERE kind = 2;");
            migrationBuilder.DropTable(
                name: "message_previews");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_previews",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    image_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
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
        }
    }
}
