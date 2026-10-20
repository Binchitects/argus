using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RoomReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnswerMode",
                table: "conversations");

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToId",
                table: "queued_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyToId",
                table: "chat_messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplyToId",
                table: "queued_messages");

            migrationBuilder.DropColumn(
                name: "ReplyToId",
                table: "chat_messages");

            migrationBuilder.AddColumn<string>(
                name: "AnswerMode",
                table: "conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }
    }
}
