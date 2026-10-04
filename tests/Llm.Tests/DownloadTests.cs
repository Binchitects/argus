using System.Net;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Arena Code's builds, served by the app for the Connect your tools page.</summary>
[Collection(nameof(AppCollection))]
public sealed class DownloadTests(AppFixture app)
{
    [Fact]
    public async Task Arena_Code_builds_are_listed_with_their_checksums_and_downloaded_by_system_without_signing_in()
    {
        var dir = Directory.CreateTempSubdirectory("downloads-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "arena-code", "linux-x64"));
            Directory.CreateDirectory(Path.Combine(dir, "arena-code", "win-x64"));
            await File.WriteAllTextAsync(Path.Combine(dir, "arena-code", "linux-x64", "arena-code"), "ELF pretend");
            await File.WriteAllTextAsync(Path.Combine(dir, "arena-code", "win-x64", "arena-code.exe"), "MZ pretend!");
            await File.WriteAllTextAsync(Path.Combine(dir, "arena-code", "SHA256SUMS"), "abc123  linux-x64/arena-code\ndef456  win-x64/arena-code.exe\n");
            await using var f = app.Create(app.ConnectionStringFor("downloads_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Downloads:Directory"] = dir });
            using var http = f.CreateClient();

            var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/arena-code")).RootElement;
            var builds = list.GetProperty("builds").EnumerateArray().ToList();
            Assert.Equal(["linux-x64", "win-x64"], builds.Select(b => b.GetProperty("rid").GetString()));
            Assert.Equal("Windows (x64)", builds[1].GetProperty("system").GetString());
            Assert.Equal("arena-code.exe", builds[1].GetProperty("fileName").GetString());
            Assert.Equal(11, builds[1].GetProperty("size").GetInt64());
            Assert.Equal("abc123", builds[0].GetProperty("sha256").GetString());

            using var res = await http.GetAsync("/api/downloads/arena-code/win-x64");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("arena-code.exe", res.Content.Headers.ContentDisposition?.FileName);
            Assert.Equal("MZ pretend!", await res.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/arena-code/osx-arm64")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/downloads/arena-code/..%2F..%2Fetc")).StatusCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task An_image_without_builds_lists_none()
    {
        using var http = app.Factory.CreateClient();
        var list = JsonDocument.Parse(await http.GetStringAsync("/api/downloads/arena-code")).RootElement;
        Assert.Empty(list.GetProperty("builds").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(list.GetProperty("version").GetString()));
    }
}
