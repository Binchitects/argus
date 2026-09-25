using Argus.Configuration;

namespace Argus.Tests;

[Collection("process-state")]
public class ConfigTests
{
    static readonly (string, string?)[] Clean =
    [
        ("ARGUS_GITLAB_URL", null), ("ARGUS_GITLAB_TOKEN", null), ("ARGUS_GITLAB_USERNAME", null),
        ("ARGUS_GITLAB_PASSWORD", null), ("ARGUS_GITLAB_AUTH", null), ("ARGUS_GITLAB_VERIFY", null), ("ARGUS_GITLAB_CA_CERT", null),
    ];

    static string Write(TempDir dir, string yaml) => dir.File("config.yaml", yaml);

    const string Minimal = "gitlab:\n  url: https://gitlab.example/\n  token: t\nindex:\n  data_dir: /data\n  db_path: /data/index.db\n";

    [Fact]
    public void Loads_defaults_and_strips_the_trailing_slash()
    {
        using var env = new EnvScope(Clean);
        using var dir = new TempDir();
        var cfg = ArgusConfig.Load(Write(dir, Minimal));
        Assert.Equal("https://gitlab.example", cfg.GitLab.Url);
        Assert.Equal("token", cfg.GitLab.Auth);
        Assert.Equal(1048576, cfg.Index.MaxFileBytes);
        Assert.Equal(600, cfg.Index.RepoTimeBudgetSeconds);
        Assert.Equal(Path.Combine("/data", "packs"), cfg.PacksDir);
        Assert.Contains("node_modules", cfg.Index.ExcludeDirs);
        Assert.True(cfg.GitLab.Verify);
    }

    [Fact]
    public void The_environment_wins_over_the_file()
    {
        using var env = new EnvScope([.. Clean, ("ARGUS_GITLAB_URL", "http://other:8929"), ("ARGUS_GITLAB_TOKEN", "envtok")]);
        using var dir = new TempDir();
        var cfg = ArgusConfig.Load(Write(dir, Minimal));
        Assert.Equal("http://other:8929", cfg.GitLab.Url);
        Assert.Equal("envtok", cfg.GitLab.Token);
    }

    [Fact]
    public void A_password_in_the_file_is_refused()
    {
        using var env = new EnvScope(Clean);
        using var dir = new TempDir();
        var exc = Assert.Throws<ConfigError>(() => ArgusConfig.Load(Write(dir, Minimal.Replace("token: t", "token: t\n  password: hunter2"))));
        Assert.Contains("ARGUS_GITLAB_PASSWORD", exc.Message);
    }

    [Fact]
    public void A_username_implies_password_mode_and_needs_the_password()
    {
        using var env = new EnvScope([.. Clean, ("ARGUS_GITLAB_USERNAME", "svc")]);
        using var dir = new TempDir();
        var exc = Assert.Throws<ConfigError>(() => ArgusConfig.Load(Write(dir, Minimal)));
        Assert.Contains("password", exc.Message);
    }

    [Fact]
    public void An_empty_verify_variable_means_unset_not_false()
    {
        using var env = new EnvScope([.. Clean, ("ARGUS_GITLAB_VERIFY", "")]);
        using var dir = new TempDir();
        Assert.True(ArgusConfig.Load(Write(dir, Minimal)).GitLab.Verify);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("YES", true)]
    public void Verify_accepts_the_usual_spellings(string value, bool expected)
    {
        using var env = new EnvScope([.. Clean, ("ARGUS_GITLAB_VERIFY", value)]);
        using var dir = new TempDir();
        Assert.Equal(expected, ArgusConfig.Load(Write(dir, Minimal)).GitLab.Verify);
    }

    [Fact]
    public void An_ambiguous_verify_value_is_refused_rather_than_guessed()
    {
        using var env = new EnvScope([.. Clean, ("ARGUS_GITLAB_VERIFY", "maybe")]);
        using var dir = new TempDir();
        Assert.Throws<ConfigError>(() => ArgusConfig.Load(Write(dir, Minimal)));
    }

    [Fact]
    public void A_ca_bundle_with_verification_off_is_a_contradiction()
    {
        using var dir = new TempDir();
        var ca = dir.File("ca.pem", "x");
        using var env = new EnvScope([.. Clean, ("ARGUS_GITLAB_VERIFY", "false"), ("ARGUS_GITLAB_CA_CERT", ca)]);
        Assert.Throws<ConfigError>(() => ArgusConfig.Load(Write(dir, Minimal)));
    }

    [Fact]
    public void Missing_index_paths_are_named()
    {
        using var env = new EnvScope(Clean);
        using var dir = new TempDir();
        var exc = Assert.Throws<ConfigError>(() => ArgusConfig.Load(Write(dir, "gitlab:\n  url: u\n  token: t\nindex:\n  data_dir: /d\n")));
        Assert.Equal("index.db_path is required", exc.Message);
    }

    [Fact]
    public void Redacted_never_contains_the_secret()
    {
        var cfg = GitLabConfig.Create("https://g", "secret-token", verify: false);
        Assert.DoesNotContain("secret-token", cfg.Redacted());
        Assert.Contains("DISABLED", cfg.Redacted());
    }
}
