using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// A model's context is now per person: llama.cpp's cache is it times the people at once. A llama.cpp model saved before
    /// held its whole cache in it, shared by its slots: it keeps what each person held (its context over its slots), and so the
    /// same cache. Its settings left on llama.cpp (Left) and those a move leaves there (Move.From) alike.
    /// </summary>
    public partial class ContextPerPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                update local_models set "Context" = "Context" / greatest(1, "Parallel")
                where "EngineKind" = 'llamacpp' and "Parallel" > 1;
                update local_models set "Left" = jsonb_set("Left", '{Context}', to_jsonb(("Left"->>'Context')::int / greatest(1, ("Left"->>'Parallel')::int)))
                where "Left"->>'EngineKind' = 'llamacpp' and ("Left"->>'Parallel')::int > 1;
                update local_models set "Move" = jsonb_set("Move", '{From}', to_jsonb(jsonb_set(("Move"->>'From')::jsonb, '{Context}',
                    to_jsonb((("Move"->>'From')::jsonb->>'Context')::int / greatest(1, (("Move"->>'From')::jsonb->>'Parallel')::int)))::text))
                where "Move"->>'From' is not null and (("Move"->>'From')::jsonb->>'EngineKind') = 'llamacpp' and (("Move"->>'From')::jsonb->>'Parallel')::int > 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                update local_models set "Context" = "Context" * greatest(1, "Parallel")
                where "EngineKind" = 'llamacpp' and "Parallel" > 1;
                update local_models set "Left" = jsonb_set("Left", '{Context}', to_jsonb(("Left"->>'Context')::int * greatest(1, ("Left"->>'Parallel')::int)))
                where "Left"->>'EngineKind' = 'llamacpp' and ("Left"->>'Parallel')::int > 1;
                update local_models set "Move" = jsonb_set("Move", '{From}', to_jsonb(jsonb_set(("Move"->>'From')::jsonb, '{Context}',
                    to_jsonb((("Move"->>'From')::jsonb->>'Context')::int * greatest(1, (("Move"->>'From')::jsonb->>'Parallel')::int)))::text))
                where "Move"->>'From' is not null and (("Move"->>'From')::jsonb->>'EngineKind') = 'llamacpp' and (("Move"->>'From')::jsonb->>'Parallel')::int > 1;
                """);
        }
    }
}
