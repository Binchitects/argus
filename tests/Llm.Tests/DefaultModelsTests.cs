using System.Net;
using System.Net.Http.Json;
using Llm.Api.Models;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// "Make the default models download an option in the installer, not in the service": the app fetches nothing on its own;
/// the installer (through the internal path, with the gateway's master key) or an admin asks for the default models, and the
/// first chat model is added once its files are in the library, however they came.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class DefaultModelsTests(AppFixture app) : IDisposable
{
    private const string First = FakeHuggingFace.Repo + ":Q4_K_M";
    private readonly string _dir = Directory.CreateTempSubdirectory("defaults-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private WebApplicationFactory<Program> NewApp()
    {
        app.Engine.Reset(Path.Combine(_dir, "config", "models.ini"));
        return app.Create(app.ConnectionStringFor("defaults_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Path.Combine(_dir, "config"),
            ["Engine:LibraryDir"] = Path.Combine(_dir, "library"), ["Engine:FirstModel"] = First,
        });
    }

    private static async Task<int> DownloadsAsync(WebApplicationFactory<Program> f)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ModelDownloads.CountAsync();
    }

    [Fact]
    public async Task Nothing_is_downloaded_until_the_installer_or_an_admin_asks()
    {
        await using var f = NewApp();
        var setup = f.Services.GetRequiredService<Provisioning>();
        // On its own (at start and every ten minutes): nothing fetched, nothing added.
        Assert.Empty(await setup.RunAsync(download: false, CancellationToken.None));
        Assert.Equal(0, await DownloadsAsync(f));

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/models/defaults"));
        var chat = list.EnumerateArray().Single(m => m.GetProperty("server").GetString() == "chat");
        Assert.Equal("missing", chat.GetProperty("state").GetString());

        // The installer's option: the internal path, only with the gateway's master key.
        var b = new TestBrowser(f);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await b.Http.PostAsync(new Uri(DefaultModelEndpoints.InternalPath, UriKind.Relative), null));
        using var ask = new HttpRequestMessage(HttpMethod.Post, new Uri(DefaultModelEndpoints.InternalPath, UriKind.Relative));
        ask.Headers.Add("x-api-key", "sk-master-for-tests");
        var res = await b.Http.SendAsync(ask);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var started = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(["chat: Tiny-Q4_K_M.gguf"], started.GetProperty("started").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(1, await DownloadsAsync(f));
        Assert.Contains("model.defaults", await (await admin.GetAsync("/api/admin/audit?take=1000")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // Asked again while it goes: not twice.
        Assert.Empty((await (await admin.PostAsync("/api/admin/models/defaults")).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("started").EnumerateArray());
        Assert.Equal(1, await DownloadsAsync(f));
    }

    [Fact]
    public async Task The_first_chat_model_copied_into_the_library_is_added_and_kept_with_nothing_fetched()
    {
        await using var f = NewApp();
        // The installer's offline option: the files copied into the library, as a download leaves them.
        GgufFile.Language("qwen3", name: "Tiny").Write(Path.Combine(_dir, "library", FakeHuggingFace.Repo, "Tiny-Q4_K_M.gguf"));
        var setup = f.Services.GetRequiredService<Provisioning>();
        Assert.Empty(await setup.RunAsync(download: false, CancellationToken.None));
        Assert.Equal(0, await DownloadsAsync(f));
        await using var scope = f.Services.CreateAsyncScope();
        var model = Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().LocalModels.AsNoTracking().ToListAsync());
        Assert.Equal($"{FakeHuggingFace.Repo}/Tiny-Q4_K_M.gguf", model.File);
        Assert.Contains(model.Name, scope.ServiceProvider.GetRequiredService<ModelCatalog>().Pinned());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var chat = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models/defaults"))).EnumerateArray().Single(m => m.GetProperty("server").GetString() == "chat");
        Assert.Equal("present", chat.GetProperty("state").GetString());
    }
}
