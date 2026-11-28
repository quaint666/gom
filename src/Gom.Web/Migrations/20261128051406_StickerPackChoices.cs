using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class StickerPackChoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "everywhere",
                table: "sticker_packs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "sticker_pack_chats",
                columns: table => new
                {
                    pack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sticker_pack_chats", x => new { x.pack_id, x.conversation_id });
                    table.ForeignKey(
                        name: "fk_sticker_pack_chats_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sticker_pack_chats_pack_id",
                        column: x => x.pack_id,
                        principalTable: "sticker_packs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sticker_pack_people",
                columns: table => new
                {
                    pack_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sticker_pack_people", x => new { x.pack_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_sticker_pack_people_pack_id",
                        column: x => x.pack_id,
                        principalTable: "sticker_packs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sticker_pack_people_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sticker_pack_chats_conversation_id",
                table: "sticker_pack_chats",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_sticker_pack_people_user_id",
                table: "sticker_pack_people",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sticker_pack_chats");

            migrationBuilder.DropTable(
                name: "sticker_pack_people");

            migrationBuilder.DropColumn(
                name: "everywhere",
                table: "sticker_packs");
        }
    }
}
