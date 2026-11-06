using System.Text.Json;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Upgrading a stack whose llama.cpp models held their whole cache in their context, shared by their slots: each keeps what
/// a person held (its context over its slots), so the engine's cache stays as it was; vLLM and SGLang models, whose context
/// was already each request's, and llama.cpp models of one slot are left as they were.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ContextPerPersonMigrationTests(AppFixture app)
{
    [Fact]
    public async Task A_llama_cpp_model_keeps_what_each_person_held_and_its_cache()
    {
        var cs = app.ConnectionStringFor("perperson_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_ContextPerPerson", StringComparison.Ordinal)) - 1]);

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await V61MigrationTests.InsertAsync(conn, "local_models", new() { ["Name"] = "shared", ["File"] = "a.gguf", ["Context"] = 262144, ["Parallel"] = 4, ["KvType"] = "q8_0", ["EngineKind"] = "llamacpp" });
            await V61MigrationTests.InsertAsync(conn, "local_models", new() { ["Name"] = "alone", ["File"] = "b.gguf", ["Context"] = 65536, ["Parallel"] = 1, ["KvType"] = "q8_0", ["EngineKind"] = "llamacpp" });
            await V61MigrationTests.InsertAsync(conn, "local_models", new() { ["Name"] = "served", ["File"] = "hf/q/r@1", ["Context"] = 262144, ["Parallel"] = 16, ["KvType"] = "fp8", ["EngineKind"] = "vllm" });
            // A vLLM model that was on llama.cpp with 2 slots of 131,072 tokens' whole cache, and one moving off llama.cpp now.
            var left = JsonSerializer.Serialize(new LocalModel { Name = "served", File = "c.gguf", EngineKind = "llamacpp", Context = 131072, Parallel = 2 });
            var from = JsonSerializer.Serialize(new LocalModel { Name = "moving", File = "d.gguf", EngineKind = "llamacpp", Context = 98304, Parallel = 3 });
            await V61MigrationTests.InsertAsync(conn, "local_models", new() { ["Name"] = "moving", ["File"] = "hf/m/n@1", ["Context"] = 98304, ["Parallel"] = 16, ["KvType"] = "fp8", ["EngineKind"] = "vllm" });
            await using var set = new NpgsqlCommand("""
                update local_models set "Left" = @left::jsonb where "Name" = 'served';
                update local_models set "Move" = jsonb_build_object('From', @from, 'FromKind', 'llamacpp', 'FromEngine', 'llamacpp', 'ToKind', 'vllm', 'ToEngine', 'vllm',
                    'Phase', 'draining', 'Old', 'serving', 'WasLoaded', false, 'Since', now(), 'At', now()) where "Name" = 'moving';
                """, conn);
            set.Parameters.AddWithValue("left", left);
            set.Parameters.AddWithValue("from", from);
            await set.ExecuteNonQueryAsync();
        }
        await db.Database.MigrateAsync();

        var models = await db.LocalModels.AsNoTracking().ToDictionaryAsync(m => m.Name);
        Assert.Equal((65536, 4), (models["shared"].Context, models["shared"].Parallel));
        Assert.Equal(65536, models["alone"].Context);
        Assert.Equal(262144, models["served"].Context);
        Assert.Equal(98304, models["moving"].Context);
        Assert.Equal(65536, JsonSerializer.Deserialize<LocalModel>(models["served"].Left!)!.Context);
        Assert.Equal(32768, JsonSerializer.Deserialize<LocalModel>(models["moving"].Move!.From!)!.Context);
        // Each person holds what they held, and the engine's cache is as large as it was.
        Assert.Equal(65536, ModelCatalog.Window(models["shared"]));
        Assert.Equal(262144, ModelCatalog.CacheCells(models["shared"]));
        Assert.Contains("ctx-size = 262144\nparallel = 4\nkv-unified = true\nkv-unified-per-slot = 65536\n", ModelCatalog.Preset(models["shared"], "/library"), StringComparison.Ordinal);
    }
}
