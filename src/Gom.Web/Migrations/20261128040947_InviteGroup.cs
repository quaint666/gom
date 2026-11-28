using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class InviteGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "one_time_links",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_one_time_links_group_id",
                table: "one_time_links",
                column: "group_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_links_group",
                table: "one_time_links",
                sql: "group_id IS NULL OR kind = 'invite'");

            migrationBuilder.AddForeignKey(
                name: "fk_one_time_links_group_id",
                table: "one_time_links",
                column: "group_id",
                principalTable: "conversations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_one_time_links_group_id",
                table: "one_time_links");

            migrationBuilder.DropIndex(
                name: "ix_one_time_links_group_id",
                table: "one_time_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_links_group",
                table: "one_time_links");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "one_time_links");
        }
    }
}
