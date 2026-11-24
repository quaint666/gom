using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class Groups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "joined_seq",
                table: "conversation_members",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_members_joined",
                table: "conversation_members",
                sql: "joined_seq >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_members_joined",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "joined_seq",
                table: "conversation_members");
        }
    }
}
