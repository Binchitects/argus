using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// Time accounting: where each chat's time went, by message, as the dashboards and Accounting read it (in the schema
    /// gateway, which the dashboards' read-only role may read): the model's (an answer's rounds), the tools' (their runs,
    /// sub-agents included), and the person's (a question's wait since the message it follows ended: reading and writing,
    /// at most 30 minutes, longer being away). Times and names only, never words. Code Arena's questions synced before are
    /// given their person, as they are from now on.
    /// </summary>
    public partial class ChatTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE chat_messages m SET "AuthorId" = c."UserId"
                FROM conversations c
                WHERE c."Id" = m."ConversationId" AND m."Role" = 'user' AND m."SyncRef" IS NOT NULL AND m."AuthorId" IS NULL
                    AND (m."DetailsJson" IS NULL OR m."DetailsJson" NOT LIKE '%"note":true%');
                CREATE VIEW gateway.chat_times AS
                    SELECT m."CreatedAt" AS at, lower(u."Email") AS person, c."WorkProjectId" AS project, m."ConversationId" AS conversation,
                        m."Id" AS message, m."AnswerId" AS answer, m."Model" AS model,
                        CASE m."Role" WHEN 'assistant' THEN 'model' WHEN 'tool' THEN 'tools' ELSE 'human' END AS part,
                        CASE WHEN m."Role" = 'user'
                            THEN least(greatest(extract(epoch FROM m."CreatedAt" - (p."CreatedAt" + CASE WHEN p."SyncRef" IS NULL THEN coalesce(p."DurationMs", 0) ELSE 0 END * interval '1 millisecond')) * 1000, 0), 1800000)::integer
                            ELSE m."DurationMs" END AS ms
                    FROM chat_messages m
                    JOIN conversations c ON c."Id" = m."ConversationId"
                    LEFT JOIN chat_messages p ON p."Id" = m."ParentId"
                    LEFT JOIN "AspNetUsers" u ON u."Id" = coalesce(m."AuthorId", c."UserId")
                    WHERE c."DeletedAt" IS NULL
                        AND ((m."Role" IN ('assistant', 'tool') AND m."DurationMs" IS NOT NULL)
                            OR (m."Role" = 'user' AND m."AuthorId" IS NOT NULL AND p."Role" IN ('assistant', 'tool')));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS gateway.chat_times;");
        }
    }
}
