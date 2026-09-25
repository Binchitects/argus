using System.Text.Json.Nodes;
using Argus.Util;

namespace Argus.Tests;

/// <summary>
/// The Python behaviours the port reproduces on purpose. Expected values were
/// taken from CPython 3.11 and pydantic-core 2.x, not reasoned out.
/// </summary>
public class PythonSemanticsTests
{
    [Theory]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("a\rb\u2028c\vd", new[] { "a", "b", "c", "d" })]
    [InlineData("", new string[0])]
    [InlineData("\n", new[] { "" })]
    [InlineData("x\n\ny", new[] { "x", "", "y" })]
    public void Splitlines_matches_python(string text, string[] expected) =>
        Assert.Equal(expected, PyStr.SplitLines(text));

    [Fact]
    public void Split_treats_unit_separators_as_whitespace_like_python()
    {
        Assert.Equal(["a", "b", "c"], PyStr.SplitWhitespace(" a\u001fb\u00a0c "));
    }

    [Fact]
    public void Len_and_prefix_count_code_points_not_utf16_units()
    {
        var s = "a\U0001F600b";
        Assert.Equal(3, PyStr.Len(s));
        Assert.Equal("a\U0001F600", PyStr.Prefix(s, 2));
        Assert.Equal("\U0001F600b", PyStr.Slice(s, 1, 5));
    }

    [Theory]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a\nb", "'a\\nb'")]
    [InlineData("q\"'", "'q\"\\''")]
    public void Repr_quotes_like_python(string value, string expected) => Assert.Equal(expected, PyStr.Repr(value));

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(1e-4, "0.0001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(123456789012345678.0, "1.2345678901234568e+17")]
    [InlineData(2.5e-10, "2.5e-10")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    public void Float_matches_python_repr(double value, string expected) => Assert.Equal(expected, PyJson.Float(value));

    [Theory]
    [InlineData(1e-6, "1e-6")]
    [InlineData(1e-5, "0.00001")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e-7, "1.5e-7")]
    [InlineData(1234567.0, "1234567.0")]
    public void Pydantic_float_matches_pydantic_core(double value, string expected) => Assert.Equal(expected, PyJson.PydanticFloat(value));

    [Fact]
    public void Dumps_uses_python_separators_and_ensure_ascii()
    {
        var node = new JsonObject { ["a"] = 1, ["b"] = new JsonArray(1.5, "é"), ["c"] = null, ["d"] = true };
        Assert.Equal("{\"a\": 1, \"b\": [1.5, \"\\u00e9\"], \"c\": null, \"d\": true}", PyJson.Dumps(node));
    }

    [Fact]
    public void Indented_matches_fastmcp_text_content()
    {
        var node = new JsonObject { ["x"] = new JsonArray(), ["y"] = new JsonObject { ["z"] = "é\n" }, ["w"] = 0.0001 };
        Assert.Equal("{\n  \"x\": [],\n  \"y\": {\n    \"z\": \"é\\n\"\n  },\n  \"w\": 0.0001\n}", PyJson.Indented(node));
    }

    [Fact]
    public void Dedent_matches_textwrap()
    {
        Assert.Equal("a\n  b\n\nc", PyStr.Dedent("    a\n      b\n  \n    c"));
    }
}
