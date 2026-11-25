using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class DailyUploadIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_attachments_uploader_id",
                table: "attachments");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_uploader_id_created_at",
                table: "attachments",
                columns: new[] { "uploader_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_attachments_uploader_id_created_at",
                table: "attachments");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_uploader_id",
                table: "attachments",
                column: "uploader_id");
        }
    }
}
