using Argus.Util;

namespace Argus.Tests;

/// <summary>Comments and strings blanked, language by language, with every line and column where it was.</summary>
public sealed class CodeTextTests
{
    [Theory]
    [InlineData("csharp", "var a = Round(1); // Round\n/* Round */ var s = \"Round\";", "var a = Round(1);")]
    [InlineData("csharp", "var s = @\"a \"\"Round\"\" b\"; var t = $\"{x} Round\"; Round();", "Round();")]
    [InlineData("csharp", "var s = \"\"\"\n  Round\n  \"\"\"; var t = $\"\"\"Round {x}\"\"\"; Round();", "Round();")]
    [InlineData("csharp", "char c = '\"'; Round(\"x\");", "Round(")]
    [InlineData("java", "String s = \"\"\"\n Round\n\"\"\"; round();", "round();")]
    [InlineData("kotlin", "/* a /* Round */ Round */ round()", "round()")]
    [InlineData("python", "x = round_it(1)  # round_it\ns = '''round_it'''\nt = \"round_it\"", "x = round_it(1)")]
    [InlineData("go", "s := `Round`; r := 'R'; Round(\"Round\")", "Round(")]
    [InlineData("typescript", "const s = `Round ${x}`; const t = 'Round'; Round(); // Round", "Round();")]
    [InlineData("javascript", "const t = \"Round\"; /* Round */ Round()", "Round()")]
    [InlineData("cpp", "auto s = R\"x(Round)x\"; char c = 'R'; Round(\"Round\");", "Round(")]
    [InlineData("c", "/* Round */ int Round(void); // Round", "int Round(void);")]
    [InlineData("rust", "let s = r#\"Round\"#; fn f<'a>(x: &'a str) { round(x) } // round", "fn f<'a>(x: &'a str) { round(x) }")]
    [InlineData("proto", "import \"round.proto\"; // Round\nmessage Round {}", "message Round {}")]
    public void Only_code_is_left_and_nothing_moves(string lang, string text, string kept)
    {
        var blanked = CodeText.Blank(lang, text);
        Assert.Equal(text.Length, blanked.Length);
        Assert.Equal(text.Count(c => c == '\n'), blanked.Count(c => c == '\n'));
        Assert.Contains(kept, blanked);
        var name = kept.Contains("round_it") ? "round_it" : kept.Contains("round") ? "round" : "Round";
        // The name is left exactly where the code names it, and nowhere else.
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(kept, $@"\b{name}\b").Count,
            System.Text.RegularExpressions.Regex.Matches(blanked, $@"\b{name}\b").Count);
    }

    [Fact]
    public void Kept_strings_stay_for_imports_and_unknown_languages_are_left_as_they_are()
    {
        Assert.Equal("import \"a/b\"     ", CodeText.Blank("go", "import \"a/b\" // c", keepStrings: true));
        Assert.Equal("# Round", CodeText.Blank("markdown", "# Round"));
        Assert.False(CodeText.Reads("markdown"));
        // Every line break stays, \r too, so the lines are the same lines.
        Assert.Equal("a\r\n  \r\n  b", CodeText.Blank("csharp", "a\r\n/*\r\n*/b"));
    }
}
