using Argus.Indexing;
using Argus.Util;

namespace Argus.Tests;

public class ParserTests
{
    static List<string> Lines(string text) => PyStr.SplitLines(text);

    [Fact]
    public void A_doxygen_block_above_a_function_is_its_doc()
    {
        var src = Lines("#include <x.h>\n\n/**\n * Decode one frame.\n * Returns 0.\n */\nint decode(void);\n");
        Assert.Equal("Decode one frame.\nReturns 0.", DocComments.ForSymbol(src, 7, null));
    }

    [Fact]
    public void Line_comments_are_collected_and_one_blank_line_is_tolerated()
    {
        var src = Lines("int a;\n// First line.\n// Second line.\n\nint f(void);\n");
        Assert.Equal("First line.\nSecond line.", DocComments.LeadingComment(src, 5));
    }

    [Fact]
    public void Two_blank_lines_break_the_attachment()
    {
        var src = Lines("// Orphan.\n\n\nint f(void);\n");
        Assert.Equal("", DocComments.LeadingComment(src, 4));
    }

    [Fact]
    public void A_licence_banner_at_the_top_of_the_file_is_not_documentation()
    {
        var src = Lines("/*\n * Copyright (c) 2020 Example. All rights reserved.\n */\nint f(void);\n");
        Assert.Equal("", DocComments.LeadingComment(src, 4));
    }

    [Fact]
    public void Attributes_between_comment_and_definition_are_skipped()
    {
        var src = Lines("/// Does the thing.\n[Obsolete]\npublic void Thing() {}\n");
        Assert.Equal("Does the thing.", DocComments.LeadingComment(src, 3, "C#"));
    }

    [Fact]
    public void A_python_docstring_wins_over_a_comment_banner()
    {
        var src = Lines("# Section banner\ndef f(x):\n    \"\"\"Return twice x.\n\n    Longer text.\n    \"\"\"\n    return 2 * x\n");
        Assert.Equal("Return twice x.\n\nLonger text.", DocComments.ForSymbol(src, 2, "Python"));
    }

    [Fact]
    public void A_long_single_quoted_string_is_data_not_a_docstring()
    {
        var src = Lines("def f():\n    '" + new string('x', 80) + "'\n");
        Assert.Equal("", DocComments.Docstring(src, 1, "python"));
    }

    [Fact]
    public void Includes_skip_comments_and_duplicates()
    {
        var found = Includes.Extract("#include <stdio.h>\n  #  include \"a/b.h\"\n// #include \"no.h\"\n#include <stdio.h>\nx = \"#include <nope.h>\";\n");
        Assert.Equal([("stdio.h", 1L), ("a/b.h", 0L)], found.Select(i => (i.Raw, i.IsAngle)).ToList());
    }

    [Theory]
    [InlineData("src/a.c", "c")]
    [InlineData("inc/A.H", "cpp")]
    [InlineData("x.py", "python")]
    [InlineData("Makefile", null)]
    [InlineData(".hidden", null)]
    [InlineData("a.tar.gz", null)]
    public void Language_detection_follows_the_extension(string path, string? lang) => Assert.Equal(lang, Filters.DetectLang(path));

    [Fact]
    public void Excluded_directories_match_whole_components_only()
    {
        byte[] data = [(byte)'x'];
        Assert.False(Filters.ShouldIndex("third_party/a.c", 1, data, 100, ["third_party"]));
        Assert.True(Filters.ShouldIndex("outbound/a.c", 1, data, 100, ["out"]));
        Assert.False(Filters.ShouldIndex("a.c", 1, [0, 1], 100, []));
        Assert.False(Filters.ShouldIndex("a.c", 200, data, 100, []));
    }

    [Theory]
    [InlineData("detail::Impl", false, "a.cpp", false)]
    [InlineData("__anon123", false, "a.cpp", false)]
    [InlineData(null, false, "a.h", true)]
    [InlineData(null, true, "a.c", false)]
    [InlineData(null, false, "a.c", true)]
    public void Visibility_rules(string? scope, bool fileRestricted, string path, bool expected) =>
        Assert.Equal(expected, Ctags.IsPublicSymbol(path, scope, fileRestricted));

    [Fact]
    public void Ctags_extracts_the_python_suites_fixture_identically()
    {
        if (Ctags.Which("ctags") is null) return;
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "ctags");
        var batch = Ctags.ExtractSymbols(root, ["decoder.h", "decoder.cpp", "anon_namespace.cpp", "missing.cpp"]);
        Assert.Contains("missing.cpp", batch.Uncovered.Keys);
        Assert.Contains("decoder.h", batch.Covered);
        var header = batch.Symbols["decoder.h"];
        Assert.Equal(1, header.Single(s => s.Name == "DecodeFrame").IsPublic);
        // A header does not make a symbol public when its scope is private.
        Assert.Equal(0, header.Single(s => s.Name == "ScratchBuffer").IsPublic);
        var anon = batch.Symbols["anon_namespace.cpp"];
        Assert.Contains(anon, s => s.IsPublic == 0);
    }
}

public class WhichRepoShapeTests
{
    [Theory]
    [InlineData("diff --git a/x.c b/x.c\n--- a/x.c\n+++ b/x.c\n", "diff")]
    [InlineData("at foo (a.c:3)\nat bar (b.c:4)\n", "stack")]
    [InlineData("png_read_image", "symbol")]
    [InlineData("std::vector::push_back", "symbol")]
    [InlineData("add H.265 support to the decoder", "prose")]
    public void Shapes(string text, string shape) => Assert.Equal(shape, WhichRepo.DetectShape(text));

    [Fact]
    public void A_bare_filename_in_prose_is_a_path()
    {
        Assert.Equal(["inflate.c"], WhichRepo.ExtractPaths("the bug is in inflate.c somewhere"));
        Assert.Empty(WhichRepo.ExtractPaths("decoder. it breaks"));
    }

    [Fact]
    public void Symbols_drop_stopwords_but_keep_filenames()
    {
        var found = WhichRepo.ExtractSymbols("fix the inflate.c window in png_read_row");
        Assert.Contains("inflate.c", found);
        Assert.Contains("png_read_row", found);
        Assert.DoesNotContain("fix", found);
    }
}
