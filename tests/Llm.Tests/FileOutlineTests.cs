using System.Globalization;
using System.Text;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>A long attachment goes to the model as its start and its outline, and read_file reads a section of it by name.</summary>
public sealed class FileOutlineTests(ITestOutputHelper output)
{
    private static string Manual()
    {
        var sb = new StringBuilder("# Operations manual\n\nHow the plant runs.\n\n");
        for (var c = 1; c <= 40; c++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"## Chapter {c}: unit {c * 7}\n\n");
            for (var s = 1; s <= 3; s++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"### Step {c}.{s}\n\n");
                for (var l = 0; l < 14; l++)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"Unit {c * 7} step {s} line {l}: check the valve and write down the pressure.\n");
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static ChatAttachment Attached(string name, string text) => new() { FileName = name, ContentType = "text/plain", Text = text };

    [Fact]
    public void A_long_manual_goes_as_its_start_and_outline_and_a_section_is_found_by_its_name()
    {
        var text = Manual();
        var inline = ChatService.Inline(Attached("manual.md", text), 30_000, canReadFiles: true);
        output.WriteLine($"| manual.md, {text.Length:N0} characters | before: 30,000 characters of its start | now: {inline.Length:N0} (start and outline) |");
        Assert.True(inline.Length <= ChatService.LongFileChars + 600, inline.Length.ToString(CultureInfo.InvariantCulture));
        Assert.StartsWith("# Operations manual", inline, StringComparison.Ordinal);
        Assert.Contains("[Its outline (the sections and their lines):", inline, StringComparison.Ordinal);
        Assert.Contains("## Chapter 40: unit 280  [lines ", inline, StringComparison.Ordinal);
        Assert.Contains("or a section of the outline by its name", inline, StringComparison.Ordinal);

        var sections = FileOutline.Sections("manual.md", text);
        var chapter = FileOutline.Find(sections, "Chapter 40: unit 280", out _)!;
        var lines = text.Split('\n');
        Assert.Equal("## Chapter 40: unit 280", lines[chapter.Start - 1]);
        Assert.Equal("Unit 280 step 3 line 13: check the valve and write down the pressure.", lines[chapter.End - 1]);
        Assert.Null(FileOutline.Find(sections, "Step", out var many));
        Assert.Equal(120, many.Count);
        Assert.NotNull(FileOutline.Find(sections, "step 40.2", out _));
    }

    [Fact]
    public void Code_is_outlined_by_its_declarations_a_pdf_by_its_pages_and_a_log_by_nothing()
    {
        var go = new StringBuilder("package shop\n\n");
        for (var f = 0; f < 3; f++)
        {
            go.Append(CultureInfo.InvariantCulture, $"func Handle{f}(w Writer) error {{\n\tfmt.Println(\"x\")\n\treturn nil\n}}\n\n");
        }
        var funcs = FileOutline.Sections("shop.go", go.ToString());
        Assert.Equal(["func Handle0(w Writer) error {", "func Handle1(w Writer) error {", "func Handle2(w Writer) error {"], funcs.Select(s => s.Name));
        Assert.Equal((3, 6), (funcs[0].Start, funcs[0].End));

        var java = "public class Shop {\n    public int total(int a) {\n        return a;\n    }\n\n    private void save() {\n        store(this);\n    }\n}\n";
        Assert.Equal(["public class Shop {", "public int total(int a) {", "private void save() {"], FileOutline.Sections("Shop.java", java).Select(s => s.Name));

        var pdf = "--- page 1 ---\nIntro\n--- page 2 ---\nBody\nmore\n";
        Assert.Equal([("page 1", 1, 2), ("page 2", 3, 5)], FileOutline.Sections("report.pdf", pdf).Select(s => (s.Name, s.Start, s.End)));

        var log = string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"line {i}: all quiet"));
        var inline = ChatService.Inline(Attached("worker.log", log), 30_000, canReadFiles: true);
        Assert.DoesNotContain("outline", inline, StringComparison.Ordinal);
        Assert.True(inline.Length <= ChatService.LongFileChars + 400, inline.Length.ToString(CultureInfo.InvariantCulture));
        // Without the files tool the model cannot read on: as much as the budget holds, and no outline.
        Assert.True(ChatService.Inline(Attached("worker.log", log), 30_000, canReadFiles: false).Length > 29_000);
    }
}
