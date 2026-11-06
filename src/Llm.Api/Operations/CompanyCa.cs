using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Llm.Api.Operations;

/// <summary>
/// Configuration section "Tls": the company's own CA, for a GitLab (or an identity provider, a knowledge source)
/// whose certificate it issued. Given at install (GITLAB_CA_FILE, mounted as <see cref="CompanyCaFile"/>) or in
/// Settings (<see cref="CompanyCa"/>, live); both are trusted.
/// </summary>
public sealed class TlsOptions
{
    /// <summary>The CA's certificate as Settings has it (PEM, one or a bundle).</summary>
    public string? CompanyCa { get; set; }

    /// <summary>The install's CA file, read-only in the container; empty or missing: none.</summary>
    public string? CompanyCaFile { get; set; }
}

/// <summary>
/// The company CA the whole stack trusts besides the public ones: every HTTP client the app makes (sign-in with the
/// company's provider, a person's GitLab token linked and used, Fix it, scheduled tasks, knowledge sources), and
/// through the file it writes beside the directory (<see cref="SharedFile"/>), Argus's API client and git. Code
/// Studio's workspaces get it with each start. A certificate the system trusts stays trusted; one that is not is
/// trusted only when its chain leads to this CA and its name and dates are right.
/// </summary>
public sealed partial class CompanyCa(IOptionsMonitor<TlsOptions> options, IOptions<Identity.AuthOptions> auth, ILogger<CompanyCa> log)
{
    public const string FileName = "company-ca.pem";

    private readonly Lock _gate = new();
    private (string Key, X509Certificate2Collection Cas, string? Problem) _read = ("", [], null);

    /// <summary>Where Argus reads it (ARGUS_GITLAB_CA_EXTRA): beside the directory file both share; null without one.</summary>
    public string? SharedFile => auth.Value.DirectoryFile is { Length: > 0 } d && Path.GetDirectoryName(d) is { Length: > 0 } dir ? Path.Combine(dir, FileName) : null;

    /// <summary>The CAs given, from Settings and the install file; none when nothing is given.</summary>
    public X509Certificate2Collection Cas() => Read().Cas;

    /// <summary>Why what was given does not read as a CA, or null.</summary>
    public string? Problem => Read().Problem;

    private (string Key, X509Certificate2Collection Cas, string? Problem) Read()
    {
        var o = options.CurrentValue;
        var file = o.CompanyCaFile is { Length: > 0 } f && File.Exists(f) ? f : null;
        var key = $"{o.CompanyCa}|{file}:{(file is null ? 0 : File.GetLastWriteTimeUtc(file).Ticks)}";
        (string Key, X509Certificate2Collection Cas, string? Problem) read;
        lock (_gate)
        {
            if (key == _read.Key)
            {
                return _read;
            }
            read = _read = Parse(key, o, file);
        }
        // Changed: Argus is given it as soon as the app sees it.
        WriteShared(read.Cas);
        return read;
    }

    private (string Key, X509Certificate2Collection Cas, string? Problem) Parse(string key, TlsOptions o, string? file)
    {
        var cas = new X509Certificate2Collection();
        string? problem = null;
        foreach (var (from, pem) in new[] { ("Settings", o.CompanyCa), ("the install's file", file is null ? null : File.ReadAllText(file)) })
        {
            if (string.IsNullOrWhiteSpace(pem))
            {
                continue;
            }
            try
            {
                cas.AddRange(Chat.Tools.ServerTls.ReadCa(pem));
            }
            catch (CryptographicException ex)
            {
                problem = $"The company CA in {from} does not read as a certificate ({ex.Message.TrimEnd('.')}): it is not trusted.";
                LogUnreadable(log, problem);
            }
        }
        return (key, cas, problem);
    }

    /// <summary>The CAs as PEM, for the workspaces and Argus; empty when none is given.</summary>
    public string Pem() => Pem(Cas());

    private static string Pem(X509Certificate2Collection cas) => string.Concat(cas.Select(c => c.ExportCertificatePem() + "\n"));

    /// <summary>Whether a server's certificate is trusted: by the system, or by its chain to the company CA.</summary>
    public bool Trusts(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }
        var cas = Cas();
        // Another name, or no certificate at all, is never made right by a CA.
        if (cas.Count == 0 || certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            return false;
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(cas);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
    }

    /// <summary>Every HTTP client's handler unless it brings its own: the system's CAs and the company's, read at each connection.</summary>
    public static HttpMessageHandler Handler(IServiceProvider services)
    {
        var ca = services.GetRequiredService<CompanyCa>();
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, errors) => ca.Trusts(certificate, errors) },
        };
    }

    /// <summary>The shared file as it should be now: written when it differs (a missing directory: nothing to share with).</summary>
    public void WriteShared() => WriteShared(Cas());

    private void WriteShared(X509Certificate2Collection cas)
    {
        if (SharedFile is not { } path || !Directory.Exists(Path.GetDirectoryName(path)))
        {
            return;
        }
        var pem = Pem(cas);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == pem)
            {
                return;
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, pem);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotShared(log, ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Problem}")]
    private static partial void LogUnreadable(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The company CA could not be written for Argus to {Path}")]
    private static partial void LogNotShared(ILogger logger, Exception ex, string path);
}

/// <summary>Writes the company CA for Argus at start and whenever Settings change it.</summary>
public sealed class CompanyCaFile(CompanyCa ca, IOptionsMonitor<TlsOptions> options) : IHostedService, IDisposable
{
    private IDisposable? _watch;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ca.WriteShared();
        _watch = options.OnChange(_ => ca.WriteShared());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _watch?.Dispose();
}
