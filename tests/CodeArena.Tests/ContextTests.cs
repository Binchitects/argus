using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Memory, earlier sessions, @files, checkpoints, the person's own commands, sub-agents and skills, kept rules, the sandbox.</summary>
public sealed class ContextTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("code-arena-ctx-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AppPaths Paths => new(Path.Combine(_root, "config"), Path.Combine(_root, "data"));

    private string Project(params (string Name, string Text)[] files)
    {
        var dir = Path.Combine(_root, "project");
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Memories_are_kept_per_project_and_for_the_person_outside_the_repository()
    {
        var project = Project();
        var memory = new MemoryStore(Paths, project);
        Assert.True(memory.Add(false, "Build with ./build.sh, not make"));
        Assert.False(memory.Add(false, "build with ./build.sh, NOT make"));
        Assert.True(memory.Add(true, "Answers in British English"));
        Assert.StartsWith(Path.Combine(_root, "data", "memory"), memory.ProjectFile, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(project, ".arena", "memory.md")));
        var prompt = memory.ForPrompt();
        Assert.Contains("- Answers in British English", prompt);
        Assert.Contains("- Build with ./build.sh, not make", prompt);
        Assert.Equal("Build with ./build.sh, not make", memory.Forget(false, 1));
        Assert.Empty(memory.Read(false));
        // Another folder of the same name is another project.
        Assert.NotEqual(MemoryStore.Key(project), MemoryStore.Key(Path.Combine(_root, "other", "project")));
    }

    [Fact]
    public void Other_agents_instruction_files_are_read_the_first_of_them_per_folder()
    {
        var project = Project(("AGENTS.md", "Use tabs."), ("CLAUDE.md", "Use spaces."), ("sub/CLAUDE.md", "Here: no tests."));
        var found = SystemPrompt.Instructions(Paths, Path.Combine(project, "sub"), project);
        Assert.Equal(["Project instructions (AGENTS.md)", "Instructions for this folder (CLAUDE.md)"], found.Select(f => f.Title));
        File.WriteAllText(Path.Combine(project, "ARENA.md"), "Ours first.");
        Assert.Equal("Ours first.", SystemPrompt.Instructions(Paths, project, project).Single().Text);
    }

    [Fact]
    public void Files_named_with_at_go_with_the_message_and_other_ats_stay_as_typed()
    {
        var project = Project(("src/a.cs", "class A {}"), ("my notes.txt", "notes"), ("bin.dat", "\0\0binary"));
        var ws = new Workspace(project);
        var (text, files) = Mentions.Expand("Look at @src/a.cs and @\"my notes.txt\", mail me@example.com, @Override, @src, @bin.dat, @../outside.txt", ws);
        Assert.Equal(["src/a.cs", "my notes.txt", "src", "bin.dat"], files);
        Assert.Contains("<file path=\"src/a.cs\">\nclass A {}\n</file>", text);
        Assert.Contains("<folder path=\"src\">\na.cs\n</folder>", text);
        Assert.Contains("(binary, 8 bytes: not shown)", text);
        Assert.StartsWith("Look at @src/a.cs and", text);
        var plain = Mentions.Expand("nothing here", ws);
        Assert.Equal("nothing here", plain.Text);
        Assert.Empty(plain.Files);
    }

    [Fact]
    public void A_rewind_puts_back_the_files_the_turns_wrote_and_says_how_long_the_conversation_was()
    {
        var project = Project(("a.txt", "one"));
        var a = Path.Combine(project, "a.txt");
        var made = Path.Combine(project, "new.txt");
        var c = new Checkpoints();
        c.Begin(0, "first");
        c.BeforeWrite(a);
        File.WriteAllText(a, "two");
        c.Begin(3, "second");
        c.BeforeWrite(a);
        File.WriteAllText(a, "three");
        c.BeforeWrite(made);
        File.WriteAllText(made, "made");
        c.BeforeWrite(a);
        File.WriteAllText(a, "four");

        var (messages, restored, lost) = c.Rewind(2, files: true);
        Assert.Equal(3, messages);
        Assert.Equal("two", File.ReadAllText(a));
        Assert.False(File.Exists(made));
        Assert.Equal(2, restored.Count);
        Assert.Empty(lost);
        Assert.Single(c.Turns);

        Assert.Equal(0, c.Rewind(1, files: false).Messages);
        Assert.Equal("two", File.ReadAllText(a));
        Assert.Empty(c.Turns);
    }

    [Fact]
    public async Task Commands_sub_agents_and_skills_are_read_from_the_project_and_the_person_and_other_agents_folders()
    {
        var project = Project(
            (".arena/commands/review.md", "---\ndescription: Review a change\nargument-hint: <branch>\n---\nReview the diff of $1 against main. Notes: $ARGUMENTS"),
            (".claude/commands/git/commit.md", "Write a commit message for the staged change."),
            (".qwen/agents/tester.md", "---\nname: tester\ndescription: Finds missing tests\ntools: read_file, grep\nmodel: model-b\n---\nYou look for code without tests."),
            (".arena/skills/release/SKILL.md", "---\nname: release\ndescription: How we cut a release\n---\nBump VERSION, then tag."),
            (".arena/skills/release/checklist.md", "- changelog"));
        Directory.CreateDirectory(Path.Combine(Paths.ConfigDir, "commands"));
        File.WriteAllText(Path.Combine(Paths.ConfigDir, "commands", "review.md"), "The person's own review (the project's wins).");
        File.WriteAllText(Path.Combine(Paths.ConfigDir, "commands", "standup.md"), "Summarize yesterday's commits.");

        var x = Extensions.Load(Paths, project);
        Assert.Equal(["git:commit", "review", "standup"], x.Commands.Select(c => c.Name).Order());
        var review = x.Commands.Single(c => c.Name == "review");
        Assert.Equal("Review a change", review.Description);
        Assert.Equal("Review the diff of feature/x against main. Notes: feature/x quickly", x.Expand("/review feature/x quickly"));
        Assert.Equal("Summarize yesterday's commits.\n\nonly mine", x.Expand("/standup only mine"));
        Assert.Null(x.Expand("/nothing"));

        var tester = Assert.Single(x.Agents);
        Assert.Equal(("tester", "model-b"), (tester.Name, tester.Model));
        Assert.Equal(["read_file", "grep"], tester.Tools);
        Assert.Equal("You look for code without tests.", tester.Prompt);

        var skill = Assert.Single(x.Skills);
        Assert.Equal("How we cut a release", skill.Description);
        var tool = x.SkillTool();
        var context = new ToolContext { Workspace = new Workspace(project), Ui = new Ui(new StringReader(""), new StringWriter(), new StringWriter(), false, false) };
        var loaded = await tool.Run(new JsonObject { ["name"] = "release" }, context, CancellationToken.None);
        Assert.Contains("Bump VERSION, then tag.", loaded.Text);
        Assert.Contains("checklist.md", loaded.Text);
        Assert.Equal("- changelog", (await tool.Run(new JsonObject { ["name"] = "release", ["file"] = "checklist.md" }, context, CancellationToken.None)).Text);
        await Assert.ThrowsAsync<ToolError>(() => tool.Run(new JsonObject { ["name"] = "release", ["file"] = "../../commands/review.md" }, context, CancellationToken.None));
    }

    [Theory]
    [InlineData("run_shell(npm test*)", "run_shell", "npm test -- --watch", true)]
    [InlineData("run_shell(npm test*)", "run_shell", "npm install", false)]
    [InlineData("run_shell(rm *)", "run_shell", "make && rm -rf build", true)]
    [InlineData("run_shell(rm *)", "run_shell", "echo rm", false)]
    [InlineData("read_file(.env*)", "read_file", ".env.local", true)]
    [InlineData("edit_file", "edit_file", "src/a.cs", true)]
    [InlineData("mcp__tickets__*", "mcp__tickets__create", null, true)]
    [InlineData("mcp__tickets__*", "mcp__other__create", null, false)]
    public void A_rule_matches_its_tool_and_the_command_or_path(string rule, string tool, string? subject, bool matches)
    {
        var project = Project();
        var def = new ToolDef
        {
            Name = tool, Kind = tool == "run_shell" ? ToolKind.Shell : tool.StartsWith("mcp", StringComparison.Ordinal) ? ToolKind.Remote : ToolKind.Edit,
            Description = "", Parameters = new JsonObject(), Run = (_, _, _) => Task.FromResult(new ToolResult("")),
        };
        var args = new JsonObject();
        if (subject is not null)
        {
            args[tool == "run_shell" ? "command" : "path"] = subject;
        }
        Assert.Equal(matches, PermissionRules.Matches(rule, def, args, new Workspace(project)));
    }

    [Fact]
    public void A_project_may_deny_but_never_allow()
    {
        var rules = PermissionRules.From(
            JsonNode.Parse("""{"allow":["run_shell(npm test*)"],"deny":["run_shell(git push*)"]}"""),
            JsonNode.Parse("""{"allow":["run_shell(*)"],"deny":["read_file(secrets/*)", "not a rule ("]}"""));
        Assert.Equal(["run_shell(npm test*)"], rules.Allow);
        Assert.Equal(["run_shell(git push*)"], rules.Deny);
        Assert.Equal(["read_file(secrets/*)"], rules.ProjectDeny);
    }

    [Fact]
    public void The_sandbox_hides_secrets_and_lets_only_the_workspace_and_caches_be_written()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        Directory.CreateDirectory(Path.Combine(home, ".npm"));
        File.WriteAllText(Path.Combine(home, ".netrc"), "machine x password y");
        var project = Project();
        var config = new Config { Sandbox = "auto" };
        var env = new Dictionary<string, string?> { ["HOME"] = home, ["PATH"] = Environment.GetEnvironmentVariable("PATH") };
        var sandbox = Sandbox.Choose(config, new Workspace(project), Paths, k => env.GetValueOrDefault(k), "s1");
        if (sandbox.Kind != Sandbox.Kinds.Bubblewrap)
        {
            // No bubblewrap here: off, and said why; "on" refuses to start.
            Assert.NotNull(sandbox.Why);
            Assert.Throws<Runtime.StartException>(() => Sandbox.Choose(new Config { Sandbox = "on" }, new Workspace(project), Paths, k => env.GetValueOrDefault(k), "s1"));
            return;
        }
        var args = string.Join(' ', sandbox.BubblewrapArguments(project));
        Assert.Contains($"--bind {project} {project}", args);
        Assert.Contains($"--bind {Path.Combine(home, ".npm")} {Path.Combine(home, ".npm")}", args);
        Assert.Contains($"--tmpfs {Path.Combine(home, ".ssh")}", args);
        Assert.Contains($"--ro-bind /dev/null {Path.Combine(home, ".netrc")}", args);
        Assert.Contains($"--tmpfs {Paths.ConfigDir}", args);
        Assert.DoesNotContain("--unshare-net", args);
    }
}
