using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Tests;

/// <summary>Tool calls as models get them wrong, mended where what they meant is plain, and said with the parameters where not.</summary>
public sealed class ToolCallRepairTests
{
    private static readonly string[] Tools = ["read_file", "write_file", "edit_file", "list_dir", "glob", "grep", "run_shell", "read_result"];

    [Theory]
    [InlineData("```json\n{\"path\": \"a.cs\"}\n```", "a code fence")]
    [InlineData("{\"path\": \"a.cs\",}", "a trailing comma")]
    [InlineData("{'path': 'a.cs'}", "single quotes")]
    [InlineData("{\"path\": \"a.cs\"", "the closing brace")]
    [InlineData("Here is the call: {\"path\": \"a.cs\"}", "words before the object")]
    [InlineData("{\"path\": \"a.cs\"} and then I will edit it", "words after the object")]
    public void Arguments_that_are_nearly_JSON_are_mended(string raw, string fix)
    {
        var args = ToolCallRepair.Arguments(raw, out var said);
        Assert.Equal("a.cs", args!["path"]!.GetValue<string>());
        Assert.Contains(fix, said, StringComparison.Ordinal);
    }

    [Fact]
    public void Python_literals_raw_line_breaks_and_strings_holding_JSON_are_mended()
    {
        var python = ToolCallRepair.Arguments("{\"replace_all\": True, \"limit\": None, \"path\": \"None.cs\"}", out var fix);
        Assert.True(python!["replace_all"]!.GetValue<bool>());
        Assert.Null(python["limit"]);
        Assert.Equal("None.cs", python["path"]!.GetValue<string>());
        Assert.Contains("Python's True, False or None", fix, StringComparison.Ordinal);

        var raw = ToolCallRepair.Arguments("{\"content\": \"line one\nline two\"}", out fix);
        Assert.Equal("line one\nline two", raw!["content"]!.GetValue<string>());
        Assert.Contains("a raw line break in a string", fix, StringComparison.Ordinal);

        var twice = ToolCallRepair.Arguments("\"{\\\"path\\\": \\\"a.cs\\\"}\"", out fix);
        Assert.Equal("a.cs", twice!["path"]!.GetValue<string>());
        Assert.Equal("the arguments were a JSON string holding the object", fix);

        var wrapped = ToolCallRepair.Arguments("{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}", out fix);
        Assert.Equal("""{"path":"a.cs"}""", wrapped!.ToJsonString());
        Assert.Contains("the call's name and arguments", fix, StringComparison.Ordinal);

        // Right already: as it is, nothing said.
        Assert.Equal("""{"path":"a.cs"}""", ToolCallRepair.Arguments("""{"path":"a.cs"}""", out fix)!.ToJsonString());
        Assert.Null(fix);
        Assert.Null(ToolCallRepair.Arguments("read the file a.cs", out _));
    }

    [Theory]
    [InlineData("Bash", "run_shell")]
    [InlineData("functions.read_file", "read_file")]
    [InlineData("ReadFile", "read_file")]
    [InlineData("read-file", "read_file")]
    [InlineData("str_replace", "edit_file")]
    [InlineData("Read", "read_file")]
    [InlineData("ls", "list_dir")]
    [InlineData("read_fil", "read_file")]
    [InlineData("<|read_file|>", "read_file")]
    [InlineData("grep_search", "grep")]
    public void A_tool_named_as_another_harness_names_it_is_found(string called, string meant) =>
        Assert.Equal(meant, ToolCallRepair.ToolName(called, Tools));

    [Fact]
    public void A_name_that_means_no_one_tool_is_not_guessed()
    {
        Assert.Null(ToolCallRepair.ToolName("deploy", Tools));
        Assert.Null(ToolCallRepair.ToolName("web_search", Tools));
        // Another harness's name for a tool that is not offered.
        Assert.Null(ToolCallRepair.ToolName("bash", ["read_file", "grep"]));
        // Two equally near.
        Assert.Null(ToolCallRepair.ToolName("read_fxxx", ["read_file", "read_fyyy"]));
    }

    private static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{
          "path":{"type":"string","description":"The file."},
          "offset":{"type":"integer","description":"The first line."},
          "limit":{"type":"integer"},
          "replace_all":{"type":"boolean"},
          "output":{"type":"string","enum":["content","files","count"]}},
         "required":["path"]}
        """)!;

    [Fact]
    public void Arguments_named_and_typed_otherwise_are_fitted_to_the_schema()
    {
        var fitted = ToolCallRepair.Fit("read_file", (JsonObject)JsonNode.Parse("""{"file_path":"a.cs","start_line":"12","limit":20,"replace_all":"yes","output":"Files"}""")!, Schema);
        Assert.Null(fitted.Problem);
        Assert.Equal("""{"path":"a.cs","offset":12,"limit":20,"replace_all":true,"output":"files"}""", fitted.Args.ToJsonString());
        Assert.Equal(["file_path → path", "start_line → offset", "offset \"12\" → 12", "replace_all \"yes\" → true", "output \"Files\" → \"files\""], fitted.Fixes);

        // Any case, - for _: the same name.
        Assert.Equal("""{"path":"a.cs"}""", ToolCallRepair.Fit("read_file", (JsonObject)JsonNode.Parse("""{"Path":"a.cs"}""")!, Schema).Args.ToJsonString());
        // Right already: nothing to say.
        Assert.Empty(ToolCallRepair.Fit("read_file", (JsonObject)JsonNode.Parse("""{"path":"a.cs","offset":3}""")!, Schema).Fixes);
    }

    [Fact]
    public void What_cannot_be_fitted_is_said_with_the_tools_parameters()
    {
        var fitted = ToolCallRepair.Fit("read_file", (JsonObject)JsonNode.Parse("""{"offset":"the start"}""")!, Schema);
        Assert.Equal("""
            The call to read_file was not run: path is required; offset must be a whole number, not "the start". read_file takes a JSON object:
            - path (string, required): The file.
            - offset (integer): The first line.
            - limit (integer)
            - replace_all (boolean)
            - output (string one of "content", "files", "count")
            For example: {"path":"…"}
            """.ReplaceLineEndings("\n"), fitted.Problem);
    }
}
