using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LedgerRefusals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Requests LiteLLM refused before any model, or the app's guardrail did, that the first mirror of its log kept as
            // failures: told by their message, as LedgerImport.Outcome now tells them (it also has LiteLLM's error class).
            migrationBuilder.Sql("""
                update gateway.requests r set status = 'refused', refusal = w.why
                from (
                  select id, case
                    when error ilike '%account is disabled%' then 'disabled'
                    when error ilike '%API access is off%' then 'api_off'
                    when error like 'You have %' and error ilike '% credit%' then 'credit'
                    when error ilike '% is starting.%' or error ilike '%could not be started%' or error ilike '%waits while its GPU%'
                      or error ilike '%answers in its place%' then 'room'
                    when error like 'The request was refused%' then 'safeguards'
                    when error ilike '%Key is blocked%' then 'blocked'
                    when error ilike '%Expired Key%' or error ilike '%key expired%' then 'expired'
                    when error ilike '%No api key passed in%' or error ilike '%Invalid proxy server token%' then 'key'
                    when error ilike '%not allowed to access model%' or error ilike '%Invalid model name%' then 'model'
                  end as why
                  from gateway.requests where status = 'failed' and source_id is not null and error is not null
                ) w
                where r.id = w.id and w.why is not null;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // What was a failure before cannot be told from a refusal that always was one: they stay refusals.
        }
    }
}
