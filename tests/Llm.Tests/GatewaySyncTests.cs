using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>The gateway kept in step with the app's models: each deployment registered once, however many syncs run at once.</summary>
[Collection(nameof(AppCollection))]
public sealed class GatewaySyncTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(FakeGateway gateway) =>
        app.Create(app.ConnectionStringFor("gwsync_" + Guid.NewGuid().ToString("N")[..8]), gateway);

    private static async Task SyncAsync(WebApplicationFactory<Program> f)
    {
        await using var scope = f.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
    }

    [Fact]
    public async Task Syncs_at_once_register_a_model_once_and_a_copy_registered_twice_is_taken_away()
    {
        var gateway = new FakeGateway();
        await using var f = NewApp(gateway);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LocalModels.Add(new LocalModel { Name = "sync-model", File = "sync/Sync-Q4_K_M.gguf", Context = 8192, Parallel = 2 });
            await db.SaveChangesAsync();
        }
        int Copies() => gateway.Managed.Values.Count(m => m.Name == "sync-model");

        // The watcher's pass and an admin's save at the same moment, again and again: one deployment.
        for (var round = 0; round < 10; round++)
        {
            foreach (var id in gateway.Managed.Keys)
            {
                await gateway.DeleteModelAsync(id);
            }
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SyncAsync(f))));
            Assert.Equal(1, Copies());
        }

        // Registered twice by two replicas syncing at once: the next sync keeps one.
        var (name, fingerprint, parameters, info) = gateway.Managed.Values.Single(m => m.Name == "sync-model");
        await gateway.AddModelAsync(name, parameters, info, fingerprint);
        Assert.Equal(2, Copies());
        await SyncAsync(f);
        Assert.Equal(1, Copies());
    }
}
