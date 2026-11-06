using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// The context view: an answer's last round keeps what filled its request piece by piece (WindowJson), and a question
    /// the files the person left out of what the model reads (LeftOutFilesJson). Both empty on the rows there are.
    /// </summary>
    public partial class ContextWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LeftOutFilesJson",
                table: "chat_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowJson",
                table: "chat_messages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeftOutFilesJson",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "WindowJson",
                table: "chat_messages");
        }
    }
}
