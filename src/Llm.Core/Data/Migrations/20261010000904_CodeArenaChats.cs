using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeArenaChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginPlace",
                table: "conversations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginRef",
                table: "conversations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncRef",
                table: "chat_messages",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_UserId_Origin_OriginRef",
                table: "conversations",
                columns: new[] { "UserId", "Origin", "OriginRef" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_conversations_UserId_Origin_OriginRef",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "OriginPlace",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "OriginRef",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "SyncRef",
                table: "chat_messages");
        }
    }
}
