using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class PushLevelWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_push_level",
                table: "push_subscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_push_level",
                table: "push_subscriptions",
                sql: "level IN ('name', 'nothing', 'words')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Going back: a device that shows the words shows who and where instead.
            migrationBuilder.Sql("UPDATE push_subscriptions SET level = 'name' WHERE level = 'words'");
            migrationBuilder.DropCheckConstraint(
                name: "ck_push_level",
                table: "push_subscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_push_level",
                table: "push_subscriptions",
                sql: "level IN ('name', 'nothing')");
        }
    }
}
