using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The route table from the app's own tables: each model name and where it is answered, sent as what, with which key, how
/// many at once and its share of a pool; and what LiteLLM is given for each, unchanged (it registers nothing anew).
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class GatewayRoutesTests(AppFixture app)
{
    [Fact]
    public async Task Local_and_remote_models_are_routes_with_their_address_key_limit_and_pool()
    {
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("routes_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Url"] = "http://llamacpp:8080", ["Replicas:Enabled"] = "false",
        });
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protect = scope.ServiceProvider.GetRequiredService<RemoteServerClient>();
            db.LocalModels.Add(new LocalModel { Name = "tiny", File = "tiny.gguf", Context = 8192, Parallel = 4 });
            db.LocalModels.Add(new LocalModel { Name = "shared", File = "shared.gguf", Context = 16384, Parallel = 2 });
            // Its longest answer saved for one slot of 16,384; four slots now hold 4,096 each.
            db.LocalModels.Add(new LocalModel { Name = "crowded", File = "crowded.gguf", Context = 4096, Parallel = 4, MaxOutput = 8192 });
            db.RemoteServers.Add(new RemoteServer
            {
                Name = "gpu2", BaseUrl = "https://gpu2.example.test/v1", ApiKeyProtected = protect.Protect("remote-" + "secret"), VerifyTls = false,
                Models =
                [
                    new RemoteModel { Remote = "org/shared-awq", Name = "shared", Context = 16384, Parallel = 6 },
                    new RemoteModel { Remote = "org/big", Name = "big", Context = 65536 },
                ],
            });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
        }
        List<Deployment> routes;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            routes = await scope.ServiceProvider.GetRequiredService<GatewayRoutes>().BuildAsync(CancellationToken.None, others: false);
        }

        // llama.cpp's router, by the model's name, with the engines' key; its context per person as its window.
        var tiny = Assert.Single(routes, r => r.Name == "tiny");
        Assert.Equal((Deployment.LlamaCpp, "http://llamacpp:8080/v1", "tiny", true, (string?)null, 1), (tiny.Kind, tiny.BaseUrl, tiny.Model, tiny.EngineKey, tiny.Key, tiny.Copies));
        Assert.Equal(8192, tiny.Info["max_input_tokens"]!.GetValue<int>());
        // Its longest answer, half the window when it sets none, under both of LiteLLM's names: max_tokens is not the window.
        Assert.Equal((4096, 4096), (tiny.Info["max_output_tokens"]!.GetValue<int>(), tiny.Info["max_tokens"]!.GetValue<int>()));
        // A longest answer the window no longer holds is half the window, as when none is set: never all the window but a
        // sliver, which a client that keeps the whole longest answer free would leave its prompt.
        var crowded = Assert.Single(routes, r => r.Name == "crowded");
        Assert.Equal((4096, 2048, 2048), (crowded.Info["max_input_tokens"]!.GetValue<int>(), crowded.Info["max_output_tokens"]!.GetValue<int>(), crowded.Info["max_tokens"]!.GetValue<int>()));
        // Another server's model: its own name there, its key, its certificate not checked as asked.
        var big = Assert.Single(routes, r => r.Name == "big");
        Assert.Equal((Deployment.Remote, "https://gpu2.example.test/v1", "org/big", false, "remote-secret", (bool?)false, "gpu2"),
            (big.Kind, big.BaseUrl, big.Model, big.EngineKey, big.Key, big.VerifyTls, big.Server));
        // One name on two servers: a pool, each copy within its slots and weighed by them.
        var shared = routes.Where(r => r.Name == "shared").OrderBy(r => r.Kind, StringComparer.Ordinal).ToList();
        Assert.Equal([(Deployment.LlamaCpp, 2, 2, 2), (Deployment.Remote, 2, 6, 6)], shared.Select(r => (r.Kind, r.Copies, r.MaxParallel ?? 0, r.Weight)));
        // As the chat lists them: each window and longest answer read back.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var listed = await scope.ServiceProvider.GetRequiredService<GatewayRoutes>().ModelsAsync(CancellationToken.None);
            var t = Assert.Single(listed, m => m.Name == "tiny");
            Assert.Equal((8192, (int?)4096), (t.Context, t.MaxOutput));
        }
        // Built again, the same fingerprints: nothing changed, nothing to pass on.
        Assert.Equal(routes.Select(r => r.Fingerprint).Order(StringComparer.Ordinal), (await app.RoutesOfAsync(f)).Select(m => m.Fingerprint).Order(StringComparer.Ordinal));
    }
}
