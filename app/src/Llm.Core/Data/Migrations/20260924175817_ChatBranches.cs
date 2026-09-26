using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentLeafId",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxTokens",
                table: "conversations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "conversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "conversations",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "conversations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TopP",
                table: "conversations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "chat_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "chat_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThinkingMs",
                table: "chat_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Data",
                table: "chat_attachments",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "chat_attachments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "text");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_ConversationId_ParentId",
                table: "chat_messages",
                columns: new[] { "ConversationId", "ParentId" });

            // Chats from before branches are one line each: every message's parent
            // is the one before it, and what is on screen ends at the last one.
            migrationBuilder.Sql("""
                UPDATE chat_messages m SET "ParentId" = p.prev
                FROM (SELECT "Id", LAG("Id") OVER (PARTITION BY "ConversationId" ORDER BY "Sequence") AS prev FROM chat_messages) p
                WHERE m."Id" = p."Id";
                UPDATE conversations c SET "CurrentLeafId" =
                  (SELECT m."Id" FROM chat_messages m WHERE m."ConversationId" = c."Id" ORDER BY m."Sequence" DESC LIMIT 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_messages_ConversationId_ParentId",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "CurrentLeafId",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "MaxTokens",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "TopP",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ThinkingMs",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "Data",
                table: "chat_attachments");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "chat_attachments");
        }
    }
}
