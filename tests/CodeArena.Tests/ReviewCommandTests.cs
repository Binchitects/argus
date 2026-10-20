namespace CodeArena.Tests;

/// <summary>/review: a turn that reads the changes with git and reviews them, changing nothing.</summary>
public sealed class ReviewCommandTests
{
    [Fact]
    public void A_review_reads_the_uncommitted_changes_or_the_branch_over_its_base()
    {
        var here = Repl.ReviewPrompt("");
        Assert.Contains("git diff HEAD", here, StringComparison.Ordinal);
        Assert.DoesNotContain("...HEAD", here, StringComparison.Ordinal);
        var branch = Repl.ReviewPrompt("main");
        Assert.Contains("git diff main...HEAD", branch, StringComparison.Ordinal);
        Assert.Contains("Do not change any file.", branch, StringComparison.Ordinal);
        Assert.Contains(Repl.Commands, c => c.Name.StartsWith("/review", StringComparison.Ordinal));
    }
}
