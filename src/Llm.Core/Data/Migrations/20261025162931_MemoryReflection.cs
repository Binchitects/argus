using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemoryReflection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Proposed",
                table: "memories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "Replaces",
                table: "memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProjectKey",
                table: "conversations",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReflectedSequence",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "MemoryAuto",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // What chats said before memory read them is not read now: only what they say from here on.
            migrationBuilder.Sql("""
                UPDATE conversations c SET "ReflectedSequence" = COALESCE((SELECT max(m."Sequence") FROM chat_messages m WHERE m."ConversationId" = c."Id"), 0);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_memories_UserId_Proposed",
                table: "memories",
                columns: new[] { "UserId", "Proposed" },
                filter: "\"Proposed\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memories_UserId_Proposed",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Proposed",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Replaces",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "ProjectKey",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ReflectedSequence",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "MemoryAuto",
                table: "AspNetUsers");
        }
    }
}
