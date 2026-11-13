using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The plant (v9): sites, areas, assets and points set up by its admins, a sheet of points imported, sites seen by their groups.</summary>
[Collection(nameof(AppCollection))]
public sealed class PlantTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin)
    {
        var (id, browser, _) = await PersonWithKeyAsync(admin);
        return (id, browser);
    }

    private async Task<(Guid Id, TestBrowser Browser, string Key)> PersonWithKeyAsync(TestBrowser admin)
    {
        var name = "pl" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("apiKey").GetString()!);
    }

    /// <summary>A gateway: no session, its person's API key.</summary>
    private HttpClient Gateway(string key)
    {
        var http = app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), AllowAutoRedirect = false, HandleCookies = false });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.DefaultRequestHeaders.Add("X-Requested-With", "gateway");
        return http;
    }

    private static async Task<Guid> Made(TestBrowser b, string path, object body)
    {
        var res = await b.PostAsync(path, body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Its_admins_set_up_a_site_its_areas_assets_and_points_and_a_sheet_imports_a_whole_site()
    {
        var admin = await AdminAsync();
        var name = "Plant " + Guid.NewGuid().ToString("N")[..6];
        var site = await Made(admin, "/api/plant/sites", new { name, timeZone = "Asia/Tehran" });
        var hall = await Made(admin, $"/api/plant/sites/{site}/areas", new { name = "Hall 1", kind = "floor" });
        var line = await Made(admin, $"/api/plant/sites/{site}/areas", new { name = "Line 3", kind = "line", parentId = hall });
        // An area is never under itself.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/plant/areas/{hall}", UriKind.Relative), new { parentId = line }));
        var pump = await Made(admin, $"/api/plant/sites/{site}/assets", new { name = "Pump P-101", kind = "machine", areaId = line, vendor = "Grundfos", tags = new[] { "Cooling", "cooling" } });
        await Made(admin, $"/api/plant/assets/{pump}/points", new { name = "temperature", unit = "°C", source = "opcua", address = "ns=2;s=P101.Temp", low = 5, high = 80, highHigh = 95 });
        // A point read from a protocol says where; its limits go up.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/plant/assets/{pump}/points", new { name = "flow", source = "modbus" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/plant/assets/{pump}/points", new { name = "flow", low = 50, high = 10 }));

        // A sheet: two assets more, one point changed, one new; importing it again makes nothing new.
        var csv = "area,asset,kind,point,unit,type,source,address,low,high\n" +
                  "Line 3,Pump P-101,machine,temperature,°C,number,opcua,ns=2;s=P101.Temp,5,85\n" +
                  "Line 3,Pump P-101,machine,state,,state,opcua,ns=2;s=P101.State,,\n" +
                  "Yard,Camera C-7,camera,online,,boolean,snmp,1.3.6.1.2.1.1.3.0,,\n" +
                  "\"Hall 1, east\",Door D-2,door,open,,boolean,mqtt,site/hall1/door2/open,,\n";
        var imported = await admin.JsonAsync(await admin.PostAsync($"/api/plant/sites/{site}/import", new { csv }));
        Assert.Equal((2, 2, 3), (imported.GetProperty("areas").GetInt32(), imported.GetProperty("assets").GetInt32(), imported.GetProperty("points").GetInt32()));
        var again = await admin.JsonAsync(await admin.PostAsync($"/api/plant/sites/{site}/import", new { csv }));
        Assert.Equal((0, 0, 0), (again.GetProperty("areas").GetInt32(), again.GetProperty("assets").GetInt32(), again.GetProperty("points").GetInt32()));
        // A sheet with a wrong line keeps nothing, and says which.
        var wrong = await admin.PostAsync($"/api/plant/sites/{site}/import", new { csv = "asset,point,source\nPump P-202,speed,modbus\n" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, wrong);
        Assert.Contains("line 2", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var read = await admin.JsonAsync(await admin.GetAsync($"/api/plant/sites/{site}"));
        Assert.Equal(["Camera C-7", "Door D-2", "Pump P-101"], read.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(["cooling"], read.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Pump P-101").GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var asset = await admin.JsonAsync(await admin.GetAsync($"/api/plant/assets/{pump}"));
        var temperature = asset.GetProperty("points").EnumerateArray().Single(pt => pt.GetProperty("name").GetString() == "temperature");
        Assert.Equal((85.0, 95.0), (temperature.GetProperty("high").GetDouble(), temperature.GetProperty("highHigh").GetDouble()));

        // Removing a site takes its areas, assets and points with it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/plant/sites/{site}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/plant/assets/{pump}"));
    }

    [Fact]
    public async Task A_site_is_seen_by_its_groups_and_only_its_admins_change_it()
    {
        var admin = await AdminAsync();
        var (annId, ann) = await PersonAsync(admin);
        var (_, ben) = await PersonAsync(admin);
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Plant ops " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { annId } }));
        var open = await Made(admin, "/api/plant/sites", new { name = "Open " + Guid.NewGuid().ToString("N")[..6] });
        var theirs = await Made(admin, "/api/plant/sites", new { name = "Ops only " + Guid.NewGuid().ToString("N")[..6], groupIds = new[] { group } });

        static async Task<List<Guid>> Seen(TestBrowser b) => [.. (await b.JsonAsync(await b.GetAsync("/api/plant"))).GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("id").GetGuid())];
        Assert.Contains(theirs, await Seen(ann));
        Assert.Contains(open, await Seen(ben));
        Assert.DoesNotContain(theirs, await Seen(ben));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/plant/sites/{theirs}"));
        Assert.False((await ann.JsonAsync(await ann.GetAsync("/api/plant"))).GetProperty("mayManage").GetBoolean());
        // Seeing is not setting up.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.PostAsync($"/api/plant/sites/{theirs}/assets", new { name = "Mine" }));
    }

    [Fact]
    public async Task A_gateway_with_plant_write_sends_values_to_the_sites_it_sees_and_they_read_back_as_series_and_latest()
    {
        var admin = await AdminAsync();
        var (gatewayId, _, key) = await PersonWithKeyAsync(admin);
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Gateways " + Guid.NewGuid().ToString("N")[..6] }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { gatewayId } }));
        var site = await Made(admin, "/api/plant/sites", new { name = "Fed " + Guid.NewGuid().ToString("N")[..6], groupIds = new[] { group } });
        var hidden = await Made(admin, "/api/plant/sites", new { name = "Not theirs " + Guid.NewGuid().ToString("N")[..6], groupIds = new[] { Guid.NewGuid() } });
        var tag = Guid.NewGuid().ToString("N")[..8];
        var pump = await Made(admin, $"/api/plant/sites/{site}/assets", new { name = "Pump" });
        var temperature = await Made(admin, $"/api/plant/assets/{pump}/points", new { name = "temperature", unit = "°C", source = "opcua", address = $"ns=2;s={tag}.T" });
        var running = await Made(admin, $"/api/plant/assets/{pump}/points", new { name = "running", dataType = "boolean", source = "mqtt", address = $"site/{tag}/running" });
        var note = await Made(admin, $"/api/plant/assets/{pump}/points", new { name = "note", dataType = "text" });
        var other = await Made(admin, $"/api/plant/sites/{hidden}/assets", new { name = "Pump" });
        var theirs = await Made(admin, $"/api/plant/assets/{other}/points", new { name = "temperature", source = "opcua", address = $"ns=2;s={tag}.T" });

        var gateway = Gateway(key);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        object Batch() => new
        {
            samples = new object[]
            {
                new { point = temperature, at = t0, value = 20.0 },
                new { source = "opcua", address = $"ns=2;s={tag}.T", at = t0.AddMinutes(1), value = 30.0 },
                new { address = $"site/{tag}/running", at = t0, on = true },
                new { address = $"site/{tag}/running", at = t0.AddMinutes(20), text = "off" },
                new { point = note, at = t0, text = "Bearing noise" },
                new { point = Guid.NewGuid(), value = 1.0 },
                new { point = temperature, at = DateTimeOffset.UtcNow.AddHours(1), value = 99.0 },
                new { point = temperature, at = t0.AddMinutes(2), text = "warm" },
            },
        };
        // Without plant.write, a key sends nothing.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await gateway.PostAsJsonAsync("/api/plant/samples", Batch()));
        var role = (await admin.JsonAsync(await admin.PostAsync("/api/admin/access/roles", new { name = "Plant gateway " + tag, permissions = new[] { "plant.write", "feature.plant" } }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/access/assignments", new { roleId = role, subjectKind = "Person", subjectId = gatewayId }));

        var res = await gateway.PostAsJsonAsync("/api/plant/samples", Batch());
        await StatusAssert.Is(HttpStatusCode.OK, res);
        var sent = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        // The hidden site's point at the same address gets nothing; a moment ahead and a number as words are wrong.
        Assert.Equal((5, 1, 2), (sent.GetProperty("kept").GetInt32(), sent.GetProperty("unknownCount").GetInt32(), sent.GetProperty("wrongCount").GetInt32()));
        // The same moment sent again replaces its value.
        await StatusAssert.Is(HttpStatusCode.OK, await gateway.PostAsJsonAsync("/api/plant/samples", new { samples = new[] { new { point = temperature, at = t0, value = 22.0 } } }));

        var series = await admin.JsonAsync(await admin.GetAsync($"/api/plant/points/{temperature}/series?from={Uri.EscapeDataString(t0.AddMinutes(-1).ToString("O"))}&to={Uri.EscapeDataString(t0.AddMinutes(59).ToString("O"))}&buckets=1"));
        var bucket = Assert.Single(series.GetProperty("buckets").EnumerateArray());
        Assert.Equal((26.0, 22.0, 30.0, 30.0, 2), (bucket.GetProperty("avg").GetDouble(), bucket.GetProperty("min").GetDouble(), bucket.GetProperty("max").GetDouble(), bucket.GetProperty("last").GetDouble(), bucket.GetProperty("n").GetInt32()));
        var texts = await admin.JsonAsync(await admin.GetAsync($"/api/plant/points/{note}/series"));
        Assert.Equal(["Bearing noise"], texts.GetProperty("texts").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.GetAsync($"/api/plant/points/{temperature}/series?from={Uri.EscapeDataString(t0.ToString("O"))}&to={Uri.EscapeDataString(t0.AddDays(-1).ToString("O"))}"));

        var asset = await admin.JsonAsync(await admin.GetAsync($"/api/plant/assets/{pump}"));
        double? Latest(string name) => asset.GetProperty("points").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name).GetProperty("latest") is { ValueKind: JsonValueKind.Object } l
            && l.GetProperty("value") is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;
        Assert.Equal((30.0, 0.0), (Latest("temperature"), Latest("running")));
        var hiddenAsset = await admin.JsonAsync(await admin.GetAsync($"/api/plant/assets/{other}"));
        Assert.Equal(JsonValueKind.Null, hiddenAsset.GetProperty("points")[0].GetProperty("latest").ValueKind);
        _ = theirs;
    }

    [Fact]
    public void A_value_is_kept_as_its_points_type_keeps_it()
    {
        var number = new Llm.Core.Plant.PlantPoint { Name = "t" };
        var flag = new Llm.Core.Plant.PlantPoint { Name = "f", DataType = "boolean" };
        Assert.Equal(1.5, Llm.Api.Plant.PlantSamples.Sample(number, new(Text: "1.5"), out _)!.Value);
        Assert.Null(Llm.Api.Plant.PlantSamples.Sample(number, new(Value: double.NaN), out var why));
        Assert.Equal("not a number", why);
        Assert.Equal(1, Llm.Api.Plant.PlantSamples.Sample(flag, new(Value: 7), out _)!.Value);
        Assert.Equal(0, Llm.Api.Plant.PlantSamples.Sample(flag, new(Text: "Off"), out _)!.Value);
        Assert.Null(Llm.Api.Plant.PlantSamples.Sample(flag, new(Text: "maybe"), out _));
    }

    [Fact]
    public void A_sheets_fields_are_split_by_commas_a_quoted_one_whole()
    {
        Assert.Equal(["Hall 1, east", "Door \"D\"", "", "x"], Llm.Api.Plant.PlantEndpoints.Fields("\"Hall 1, east\",\"Door \"\"D\"\"\",,x"));
    }
}
