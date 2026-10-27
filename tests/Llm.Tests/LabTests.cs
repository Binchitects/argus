using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>The science lab: notebooks made as lab chats of a field, listed in the lab with what was checked in each.</summary>
[Collection(nameof(AppCollection))]
public sealed class LabTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?>? settings = null) =>
        app.Create(app.ConnectionStringFor("lab_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?>(settings ?? []) { ["Auth:DataKey"] = "a-data-key-for-lab-tests" });

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "l" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    [Fact]
    public async Task A_notebook_of_a_field_has_its_instructions_and_tools_is_listed_in_the_lab_not_the_chats_and_counts_its_checks()
    {
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(new() { ["Sandbox:Dir"] = sandbox.Dir });
        var (b, _) = await PersonAsync(f);

        var fields = await b.JsonAsync(await b.GetAsync("/api/lab/fields"));
        Assert.True(fields.GetProperty("available").GetBoolean());
        Assert.Equal(["mathematics", "physics", "chemistry", "statistics", "biology", "general"], fields.GetProperty("fields").EnumerateArray().Select(x => x.GetProperty("id").GetString()));

        var res = await b.PostAsync("/api/chat/conversations", new { lab = "mathematics" });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var notebook = await b.JsonAsync(res);
        var id = notebook.GetProperty("id").GetGuid();
        Assert.Equal("mathematics", notebook.GetProperty("labField").GetString());
        Assert.Equal(["lab", "python"], notebook.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).Order());
        Assert.StartsWith("You are a mathematician working a problem in the science lab.", notebook.GetProperty("systemPrompt").GetString(), StringComparison.Ordinal);

        // Two checks: one proved, one refuted (the fake sandbox proves a side equal to itself).
        await SendAsync(b, id, """Check [call math_check {"left":"x","right":"x"}]""");
        await SendAsync(b, id, """Check [call math_check {"left":"x","right":"x + 1"}]""");
        var listed = (await b.JsonAsync(await b.GetAsync("/api/lab/notebooks"))).EnumerateArray().Single();
        Assert.Equal(id, listed.GetProperty("id").GetGuid());
        Assert.Equal("mathematics", listed.GetProperty("field").GetString());
        Assert.Equal((1, 1, 0), (listed.GetProperty("checks").GetProperty("proved").GetInt32(), listed.GetProperty("checks").GetProperty("refuted").GetInt32(),
            listed.GetProperty("checks").GetProperty("numeric").GetInt32()));
        // Not among the chats; read as any chat is.
        Assert.DoesNotContain((await b.JsonAsync(await b.GetAsync("/api/chat/conversations"))).EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);
        await StatusAssert.Is(HttpStatusCode.OK, await b.GetAsync($"/api/chat/conversations/{id}"));
    }

    [Fact]
    public async Task Without_the_sandbox_there_is_no_lab_and_the_page_says_why_and_an_unknown_field_is_refused()
    {
        await using var f = NewApp();
        var (b, _) = await PersonAsync(f);
        var fields = await b.JsonAsync(await b.GetAsync("/api/lab/fields"));
        Assert.False(fields.GetProperty("available").GetBoolean());
        Assert.Contains("the sandbox running", fields.GetProperty("why").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Conflict, await b.PostAsync("/api/chat/conversations", new { lab = "mathematics" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/chat/conversations", new { lab = "alchemy" }));
    }

    [Fact]
    public async Task A_proof_is_checked_by_lean_only_when_lean_accepts_it_with_no_sorry()
    {
        await using var sandbox = new FakeSandbox();
        await using var lean = new FakeSandbox();
        await using var f = NewApp(new() { ["Sandbox:Dir"] = sandbox.Dir, ["Lean:Dir"] = lean.Dir });
        var (b, _) = await PersonAsync(f);
        // A mathematics notebook has Lean's proofs when the checker runs.
        var notebook = await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { lab = "mathematics" }));
        var id = notebook.GetProperty("id").GetGuid();
        Assert.Contains("proofs", notebook.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));

        static string Call(string source) => "Prove [call lean_check " + JsonSerializer.Serialize(new { source }) + "]";
        var verdicts = new List<JsonElement>();
        foreach (var source in new[]
        {
            "import Mathlib\n\ntheorem t (a b : ℝ) : a + b = b + a := by ring",
            "import Mathlib\n\ntheorem t (a b : ℝ) : a + b = b + a := by sorry",
            "import Mathlib\n\ntheorem wrong (a b : ℝ) : a + b = b + a + 1 := by ring",
            // One line, with \n for its line breaks: read with them.
            "import Mathlib\\n\\ntheorem t (a b : ℝ) : a + b = b + a := by ring",
        })
        {
            var result = (await SendAsync(b, id, Call(source))).Single(e => e.GetProperty("type").GetString() == "tool_result");
            verdicts.Add(JsonDocument.Parse(result.GetProperty("text").GetString()!).RootElement);
        }
        Assert.Equal(["checked", "incomplete", "rejected", "checked"], verdicts.Select(v => v.GetProperty("verdict").GetString()));
        Assert.StartsWith("import Mathlib\n\ntheorem t", lean.Jobs[3]["code"]!.GetValue<string>(), StringComparison.Ordinal);
        var message = verdicts[2].GetProperty("messages")[0];
        Assert.Equal((3, 52, "error"), (message.GetProperty("line").GetInt32(), message.GetProperty("column").GetInt32(), message.GetProperty("severity").GetString()));
        Assert.Contains("⊢ a + b = b + a + 1", message.GetProperty("text").GetString(), StringComparison.Ordinal);
        // The Lean file went to Lean's sandbox, not Python's.
        Assert.Equal(4, lean.Jobs.Count);
        Assert.DoesNotContain(sandbox.Jobs, j => j["code"]!.GetValue<string>().StartsWith("import Mathlib", StringComparison.Ordinal));
        // Counted in the lab: two proved, one refuted; sorry proves nothing.
        var listed = (await b.JsonAsync(await b.GetAsync("/api/lab/notebooks"))).EnumerateArray().Single();
        Assert.Equal((2, 1), (listed.GetProperty("checks").GetProperty("proved").GetInt32(), listed.GetProperty("checks").GetProperty("refuted").GetInt32()));
    }

    [Fact]
    public void Leans_messages_are_read_by_line_and_column_with_what_follows_each()
    {
        var messages = Llm.Api.Lab.ProofTool.Messages(".Proof.lean:2:0: warning: unused variable `h`\n.Proof.lean:5:4: error(lean.unknownIdentifier): unknown identifier 'foo'\nmore of it\n");
        Assert.Equal([(2, 0, "warning"), (5, 4, "error")], messages.Select(m => (m["line"]!.GetValue<int>(), m["column"]!.GetValue<int>(), m["severity"]!.GetValue<string>())));
        Assert.Equal("unknown identifier 'foo'\nmore of it", messages[1]["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("math_check", """{"verdict":"proved"}""", "proved")]
    [InlineData("math_check", """{"verdict":"holds numerically"}""", "numeric")]
    [InlineData("math_check", """{"verdict":"refuted"}""", "refuted")]
    [InlineData("math_solve", """{"solutions":[{"checked":true},{"checked":true}]}""", "proved")]
    [InlineData("math_solve", """{"solutions":[{"checked":true},{"checked":false}]}""", "refuted")]
    [InlineData("chem_balance", """{"balanced":"4 Fe + 3 O2 -> 2 Fe2O3","checked":true}""", "proved")]
    [InlineData("chem_balance", """{"checked":false}""", "refuted")]
    [InlineData("lean_check", """{"verdict":"checked"}""", "proved")]
    [InlineData("lean_check", """{"verdict":"rejected"}""", "refuted")]
    [InlineData("lean_check", """{"verdict":"incomplete"}""", "other")]
    [InlineData("math_check", "not json", "other")]
    public void A_checks_verdict_is_read_from_its_result(string tool, string content, string verdict) =>
        Assert.Equal(verdict, Llm.Api.Lab.LabEndpoints.Verdict(tool, content));
}
