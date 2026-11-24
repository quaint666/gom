using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class GroupRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "role",
                table: "conversation_members",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Groups that already exist: the creator is the leader.
            migrationBuilder.Sql(
                "UPDATE conversation_members m SET role = 2 FROM conversations c " +
                "WHERE c.id = m.conversation_id AND c.kind = 'group' AND c.created_by = m.user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_members_conversation_id",
                table: "conversation_members",
                column: "conversation_id",
                unique: true,
                filter: "role = 2");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conversation_members_role",
                table: "conversation_members",
                sql: "role IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversation_members_conversation_id",
                table: "conversation_members");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conversation_members_role",
                table: "conversation_members");

            migrationBuilder.DropColumn(
                name: "role",
                table: "conversation_members");
        }
    }
}
