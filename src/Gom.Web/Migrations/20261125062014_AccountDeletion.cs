using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class AccountDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deleted_by",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_deleted_by",
                table: "users",
                column: "deleted_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_deleted",
                table: "users",
                sql: "deleted_at IS NULL OR (disabled_at IS NOT NULL AND NOT is_owner)");

            migrationBuilder.AddForeignKey(
                name: "fk_users_deleted_by",
                table: "users",
                column: "deleted_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_deleted_by",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_deleted_by",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_deleted",
                table: "users");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "deleted_by",
                table: "users");
        }
    }
}
