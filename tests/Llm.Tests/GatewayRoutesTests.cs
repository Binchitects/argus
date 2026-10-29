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
            db.LocalModels.Add(new LocalModel { Name = "tiny", File = "tiny.gguf", Context = 32768, Parallel = 4 });
            db.LocalModels.Add(new LocalModel { Name = "shared", File = "shared.gguf", Context = 16384, Parallel = 2 });
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
            routes = await scope.ServiceProvider.GetRequiredService<GatewayRoutes>().BuildAsync(CancellationToken.None);
        }

        // llama.cpp's router, by the model's name, with the engines' key; a slot's share of the context as its window.
        var tiny = Assert.Single(routes, r => r.Name == "tiny");
        Assert.Equal((Deployment.LlamaCpp, "http://llamacpp:8080/v1", "tiny", true, (string?)null, 1), (tiny.Kind, tiny.BaseUrl, tiny.Model, tiny.EngineKey, tiny.Key, tiny.Copies));
        Assert.Equal(8192, tiny.Info["max_input_tokens"]!.GetValue<int>());
        // Another server's model: its own name there, its key, its certificate not checked as asked.
        var big = Assert.Single(routes, r => r.Name == "big");
        Assert.Equal((Deployment.Remote, "https://gpu2.example.test/v1", "org/big", false, "remote-secret", (bool?)false, "gpu2"),
            (big.Kind, big.BaseUrl, big.Model, big.EngineKey, big.Key, big.VerifyTls, big.Server));
        // One name on two servers: a pool, each copy within its slots and weighed by them.
        var shared = routes.Where(r => r.Name == "shared").OrderBy(r => r.Kind, StringComparer.Ordinal).ToList();
        Assert.Equal([(Deployment.LlamaCpp, 2, 2, 2), (Deployment.Remote, 2, 6, 6)], shared.Select(r => (r.Kind, r.Copies, r.MaxParallel ?? 0, r.Weight)));
        // LiteLLM was given exactly these, by these fingerprints: nothing is registered anew.
        Assert.Equal(routes.Select(r => r.Fingerprint).Order(StringComparer.Ordinal), gateway.Managed.Values.Select(m => m.Fingerprint).Order(StringComparer.Ordinal));
    }
}
