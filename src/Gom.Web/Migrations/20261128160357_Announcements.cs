using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Announcements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_kind",
                table: "conversations");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_kind",
                table: "conversations",
                column: "kind",
                unique: true,
                filter: "kind = 'announce'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_kind",
                table: "conversations",
                sql: "kind IN ('direct', 'group', 'announce')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversations_kind",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversations_kind",
                table: "conversations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversations_kind",
                table: "conversations",
                sql: "kind IN ('direct', 'group')");
        }
    }
}
