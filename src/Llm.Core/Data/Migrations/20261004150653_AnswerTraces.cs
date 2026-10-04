using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnswerTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AnswerMs",
                table: "chat_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceJson",
                table: "chat_messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_Answered",
                table: "chat_messages",
                column: "CreatedAt",
                filter: "\"AnswerMs\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_messages_Answered",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "AnswerMs",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "TraceJson",
                table: "chat_messages");
        }
    }
}
