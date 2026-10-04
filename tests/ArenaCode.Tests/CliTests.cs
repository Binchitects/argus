using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace ArenaCode.Tests;

public sealed class CliTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    [Fact]
    public async Task Login_checks_the_key_with_the_gateway_and_saves_it_readable_by_its_owner_only()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Clear());
        h.Secret = FakeGateway.Key;
        Assert.Equal(0, await h.Run("", "login", "--url", _mcp.BaseUrl, "--gateway", _gateway.Url));

        Assert.Contains("takes your key: 2 models (model-a, model-b)", h.Out);
        Assert.Contains("Arena's tools: 3 (web_search, read_file, create_issue)", h.Out);
        Assert.Contains("Signed in to " + _mcp.BaseUrl, h.Out);
        Assert.DoesNotContain(FakeGateway.Key, h.Out + h.Err);
        var saved = JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!;
        Assert.Equal(FakeGateway.Key, saved["apiKey"]!.GetValue<string>());
        Assert.Equal(_mcp.BaseUrl, saved["url"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(h.Paths.ConfigFile));
        }

        // And the session works from what was saved.
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.Equal("Hello from the model.", h.Out.Trim());

        Assert.Equal(0, await h.Run("", "logout"));
        Assert.Null(JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["apiKey"]);
        Assert.Equal(_mcp.BaseUrl, JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_asks_for_the_address_and_a_refused_key_saves_nothing()
    {
        using var h = new Harness(_gateway, _mcp, c => c.Clear());
        h.Secret = "sk-wrong";
        Assert.Equal(1, await h.Run(_mcp.BaseUrl + "\n", "login", "--gateway", _gateway.Url));
        Assert.Contains("Your Arena's address", h.Out);
        Assert.Contains("The gateway refused your API key (401)", h.Err);
        Assert.Null(JsonNode.Parse(File.ReadAllText(h.Paths.ConfigFile))!["apiKey"]);
    }

    [Fact]
    public async Task The_gateway_is_found_at_gateway_dot_the_domain()
    {
        Assert.Equal("https://gateway.llm.example.com", Config.DeriveGateway("https://llm.example.com"));
        Assert.Equal("https://gateway.llm.localhost:8443", Config.DeriveGateway("https://llm.localhost:8443/"));
        Assert.Equal("https://llm.example.com", Config.NormalizeUrl("llm.example.com/"));
        var config = new Config { Url = "https://llm.example.com", Gateway = "https://ai.example.com/v1/" };
        Assert.Equal("https://ai.example.com", config.GatewayUrl);
        Assert.Equal("https://llm.example.com/mcp", config.ArenaMcpUrl);
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "--version"));
        Assert.StartsWith("arena-code ", h.Out);
        Assert.Equal(2, await h.Run("", "--bogus"));
    }

    [Fact]
    public void The_command_line_is_read_as_people_type_it()
    {
        var o = Cli.Parse(["-p", "fix", "the", "bug", "--mode", "plan", "-c", "--add-dir", "../lib"]);
        Assert.True(o.Print);
        Assert.Equal("fix the bug", o.Prompt);
        Assert.Equal("plan", o.Mode);
        Assert.True(o.Continue);
        Assert.Equal(["../lib"], o.AddDirs);
        var resume = Cli.Parse(["--resume", "20261004-101500-ab12cd", "carry", "on"]);
        Assert.Equal("20261004-101500-ab12cd", resume.ResumeId);
        Assert.Equal("carry on", resume.Prompt);
        Assert.Null(Cli.Parse(["--resume", "carry on"]).ResumeId);
        Assert.Equal("-", Cli.Parse(["-p", "-"]).Prompt);
        Assert.Equal("login", Cli.Parse(["login", "--url", "x"]).Command);
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--thinking", "max"]));
    }

    [Fact]
    public void A_private_CA_is_trusted_and_a_certificate_for_another_name_is_not()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Arena Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=llm.example.com", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var leaf = leafRequest.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3, 4]);

        var dir = Directory.CreateTempSubdirectory("arena-ca-").FullName;
        try
        {
            var file = Path.Combine(dir, "ca.crt");
            File.WriteAllText(file, ca.ExportCertificatePem());
            var roots = Net.LoadCertificates(file);
            Assert.Single(roots);
            Assert.True(Net.Trusted(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, roots));
            Assert.False(Net.Trusted(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, roots));

            using var otherKey = RSA.Create(2048);
            var other = new CertificateRequest("CN=Someone Else", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            other.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var otherCa = other.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            Assert.False(Net.Trusted(leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, [otherCa]));

            // --ca first, then ARENA_CA_CERT, the config, SSL_CERT_FILE.
            var env = new Dictionary<string, string?> { ["ARENA_CA_CERT"] = "/env/ca.crt", ["SSL_CERT_FILE"] = "/ssl/bundle.pem" };
            Assert.Equal("/opt/ca.crt", Net.CaFile("/opt/ca.crt", "/config/ca.crt", k => env.GetValueOrDefault(k)));
            Assert.Equal("/env/ca.crt", Net.CaFile(null, "/config/ca.crt", k => env.GetValueOrDefault(k)));
            env.Remove("ARENA_CA_CERT");
            Assert.Equal("/config/ca.crt", Net.CaFile(null, "/config/ca.crt", k => env.GetValueOrDefault(k)));
            Assert.Equal("/ssl/bundle.pem", Net.CaFile(null, null, k => env.GetValueOrDefault(k)));
            Assert.Throws<FileNotFoundException>(() => Net.LoadCertificates(Path.Combine(dir, "missing.crt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Config_and_data_folders_follow_each_system()
    {
        var env = new Dictionary<string, string?> { ["HOME"] = "/home/ada", ["XDG_CONFIG_HOME"] = null };
        var paths = AppPaths.From(k => env.GetValueOrDefault(k));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("/home/ada/.config/arena-code", paths.ConfigDir);
            Assert.Equal("/home/ada/.local/share/arena-code/sessions", paths.SessionsDir);
        }
        env["ARENA_CODE_HOME"] = "/portable";
        Assert.Equal(Path.Combine("/portable", "config", "config.json"), AppPaths.From(k => env.GetValueOrDefault(k)).ConfigFile);
    }

    [Fact]
    public void The_system_prompt_carries_the_environment_the_mode_and_the_instructions()
    {
        var root = Directory.CreateTempSubdirectory("arena-prompt-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
            File.WriteAllText(Path.Combine(root, "repo", ".git", "HEAD"), "ref: refs/heads/feature/x\n");
            File.WriteAllText(Path.Combine(root, "repo", "ARENA.md"), "Run make test before finishing.");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "ARENA.md"), "I prefer short answers.");
            var inputs = new SystemPrompt.Inputs
            {
                Workspace = new Workspace(Path.Combine(root, "repo")),
                Paths = new AppPaths(Path.Combine(root, "config"), Path.Combine(root, "data")),
                Mode = () => Mode.Plan,
                Model = () => "model-a",
            };
            var prompt = SystemPrompt.Build(inputs, now: new DateTime(2026, 10, 4));
            Assert.Contains("branch feature/x", prompt);
            Assert.Contains("Date: 2026-10-04 (Sunday)", prompt);
            Assert.Contains("Mode: plan", prompt);
            Assert.Contains("You cannot edit files or run commands now", prompt);
            Assert.Contains("Run make test before finishing.", prompt);
            Assert.Contains("I prefer short answers.", prompt);
            Assert.Contains("Model: model-a", prompt);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }
}
