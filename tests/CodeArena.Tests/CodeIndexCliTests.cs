using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>code-arena index and code-arena search: the code index from the command line, with no model and no sign-in.</summary>
public sealed class CodeIndexCliTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly Harness _h;

    public CodeIndexCliTests()
    {
        _h = new Harness(_gateway, null, c => c.Clear());
        Write("src/Shop/Payments.cs", "namespace Shop;\n\npublic sealed class Payments\n{\n    /// <summary>Gives the money back to the card.</summary>\n    public void Refund(decimal amount) { }\n\n    public void Run() { }\n}\n");
        Write("src/Jobs/Nightly.cs", "namespace Jobs;\n\npublic sealed class Nightly\n{\n    public void Run() { }\n}\n");
        Write("src/Shop/Cart.cs", "namespace Shop;\n\npublic sealed class Cart\n{\n    public void Back(Payments p) => p.Refund(1);\n}\n");
        Write(".env", "TOKEN=x\n");
    }

    public void Dispose()
    {
        _h.Dispose();
        _gateway.Dispose();
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(_h.Work, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public async Task Index_says_what_it_holds_and_keeps_it_outside_the_folder()
    {
        Assert.Equal(0, await _h.Run("", "index"));
        Assert.Contains("4 files, ", _h.Out, StringComparison.Ordinal);
        Assert.Contains("Built in", _h.Out, StringComparison.Ordinal);
        Assert.Contains("Skipped as secrets (listed, never read): .env.", _h.Out, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(_h.Paths.DataDir, "index"), "*.bin"));
        Assert.Empty(Directory.GetFiles(_h.Work, "*.bin", SearchOption.AllDirectories));

        Assert.Equal(0, await _h.Run("", "index"));
        Assert.Contains("Synced in", _h.Out, StringComparison.Ordinal);
        Assert.Contains("0 read, 4 kept", _h.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_answers_by_words_by_name_and_by_symbol_with_json_for_programs()
    {
        Assert.Equal(0, await _h.Run("", "search", "money back to the card"));
        Assert.Contains("[1] src/Shop/Payments.cs:6-6 · method Payments.Refund", _h.Out, StringComparison.Ordinal);

        Assert.Equal(0, await _h.Run("", "search", "Refund", "--json"));
        var json = JsonNode.Parse(_h.Out.Trim())!;
        Assert.Equal("name", json["route"]!.GetValue<string>());
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Contains(json["items"]!.AsArray(), i => i!["path"]!.GetValue<string>() == "src/Shop/Cart.cs" && i["kind"]!.GetValue<string>() == "use" && i["start"]!.GetValue<int>() == 5);

        Assert.Equal(0, await _h.Run("", "search", "--symbol", "Payments.Refund", "--show", "callers"));
        Assert.Contains("- src/Shop/Cart.cs:5-5 · method Cart.Back · calls it at :5 (type)", _h.Out, StringComparison.Ordinal);

        // Two symbols of one name: the candidates, and 3 for a program to tell.
        Assert.Equal(3, await _h.Run("", "search", "--symbol", "Run"));
        Assert.Contains("- src/Jobs/Nightly.cs#Jobs.Nightly.Run", _h.Out, StringComparison.Ordinal);
        Assert.Contains("- src/Shop/Payments.cs#Shop.Payments.Run", _h.Out, StringComparison.Ordinal);

        Assert.Equal(0, await _h.Run("", "search", "--outline", "src/Shop/Payments.cs"));
        Assert.Contains("    6 public void Refund(decimal amount)", _h.Out, StringComparison.Ordinal);

        Assert.Equal(2, await _h.Run("", "search", "--symbol", "Run", "words too"));
    }
}
