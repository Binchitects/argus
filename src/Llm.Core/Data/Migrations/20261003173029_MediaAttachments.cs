using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Seconds",
                table: "chat_attachments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Sound",
                table: "chat_attachments",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Seconds",
                table: "chat_attachments");

            migrationBuilder.DropColumn(
                name: "Sound",
                table: "chat_attachments");
        }
    }
}
