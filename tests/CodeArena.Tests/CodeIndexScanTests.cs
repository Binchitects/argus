namespace CodeArena.Tests;

/// <summary>
/// What the code index scans: code with its comments and strings blanked (Argus's CodeText, shared as a file), every line
/// where it was, so a symbol's range in the blanked text is its range in the file, and a brace in a string counts for nothing.
/// </summary>
public sealed class CodeIndexScanTests
{
    [Theory]
    [InlineData("csharp", "var s = \"\"\"\n  { not a block }\n  \"\"\";\nclass A { }\n")]
    [InlineData("csharp", "var t = $\"{a} } {{\"; // }\nclass B { void M() { } }\n")]
    [InlineData("typescript", "const t = `a ${b} }\n}`\nfunction f() { return 1 }\n")]
    [InlineData("python", "s = '''\ndef not_a_function():\n'''\ndef real():\n    pass\n")]
    public void Blanked_code_keeps_its_lines_and_its_braces_balance(string lang, string text)
    {
        var blanked = Argus.Util.CodeText.Blank(lang, text);
        Assert.Equal(text.Split('\n').Length, blanked.Split('\n').Length);
        Assert.Equal(blanked.Count(c => c == '{'), blanked.Count(c => c == '}'));
        Assert.DoesNotContain("not a", blanked, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_strings_stay_when_asked()
    {
        Assert.Contains("\"./api\"", Argus.Util.CodeText.Blank("typescript", "import { a } from \"./api\" // where", keepStrings: true), StringComparison.Ordinal);
    }
}
