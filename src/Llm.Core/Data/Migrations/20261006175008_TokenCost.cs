using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TokenCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CachedInputPerMtok",
                table: "local_models",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AnswerId",
                table: "chat_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Cost",
                table: "chat_messages",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_AnswerId",
                table: "chat_messages",
                column: "AnswerId");

            // The answers kept so far: each round and tool call gets the answer it is part of, from the
            // round that follows the question down to the next question. Not a fork's copies (made before
            // their chat, they keep their first chat's times): that answer ran, and is listed, once.
            migrationBuilder.Sql("""
                WITH RECURSIVE answer AS (
                    SELECT m."Id", m."Id" AS "AnswerId"
                    FROM chat_messages m
                    JOIN chat_messages q ON q."Id" = m."ParentId"
                    JOIN conversations c ON c."Id" = m."ConversationId"
                    WHERE m."Role" = 'assistant' AND q."Role" = 'user' AND m."CreatedAt" >= c."CreatedAt"
                    UNION ALL
                    SELECT c."Id", a."AnswerId"
                    FROM chat_messages c
                    JOIN answer a ON c."ParentId" = a."Id"
                    WHERE c."Role" IN ('assistant', 'tool')
                )
                UPDATE chat_messages m SET "AnswerId" = a."AnswerId" FROM answer a WHERE m."Id" = a."Id";
                """);

            // A delegate call carries its sub-agents' tokens, as new ones do (they were only in its details).
            migrationBuilder.Sql("""
                UPDATE chat_messages m
                SET "PromptTokens" = s.p, "CachedTokens" = s.c, "CompletionTokens" = s.o
                FROM (
                    SELECT x."Id",
                           sum(coalesce((a->'usage'->>'prompt')::int, 0)) AS p,
                           sum(coalesce((a->'usage'->>'cached')::int, 0)) AS c,
                           sum(coalesce((a->'usage'->>'completion')::int, 0)) AS o
                    FROM (
                        SELECT "Id", "DetailsJson"::jsonb AS d
                        FROM chat_messages
                        WHERE "Role" = 'tool' AND "ToolName" = 'delegate' AND "PromptTokens" IS NULL AND "DetailsJson" IS JSON OBJECT
                    ) x
                    CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(x.d->'agents') = 'array' THEN x.d->'agents' ELSE '[]'::jsonb END) a
                    GROUP BY x."Id"
                    HAVING bool_or(jsonb_typeof(a->'usage') = 'object')
                ) s
                WHERE m."Id" = s."Id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_messages_AnswerId",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "CachedInputPerMtok",
                table: "local_models");

            migrationBuilder.DropColumn(
                name: "AnswerId",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "Cost",
                table: "chat_messages");
        }
    }
}
