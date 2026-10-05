using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Llm.Tests;

/// <summary>Certificates for tests: a CA of a company's own, server certificates it signs, and self-signed ones.</summary>
public static class TestCertificates
{
    /// <summary>A root CA that can sign server certificates.</summary>
    public static X509Certificate2 Ca(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, O=Test company", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    /// <summary>A server certificate for these names, signed by <paramref name="ca"/> (or by itself), with its key.</summary>
    public static X509Certificate2 Server(X509Certificate2? ca, params string[] names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={names[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var (from, to) = (DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        if (ca is null)
        {
            using var self = request.CreateSelfSigned(from, to);
            return X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
        }
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var signed = request.Create(ca, from, to, RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>The public certificate in PEM, as an admin pastes it.</summary>
    public static string Pem(X509Certificate2 certificate) => certificate.ExportCertificatePem();
}

/// <summary>
/// A real HTTPS server on 127.0.0.1 (Kestrel) with the certificate a test gives, passing each request
/// on to a fake (FakeMcp) as if it had been sent to <c>asHost</c>: an MCP server, the pet store, GitLab.
/// </summary>
public sealed class TlsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _requests;

    private TlsServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>Requests that got through the TLS handshake.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <param name="host">The host name in the address: localhost (the certificates' name), or 127.0.0.1.</param>
    public string Url(string path, string host = "localhost") => $"https://{host}:{Port}{path}";

    public static async Task<TlsServer> StartAsync(X509Certificate2 certificate, HttpMessageHandler fake, string asHost)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        var app = builder.Build();
        TlsServer? server = null;
        app.Run(async ctx => await server!.ForwardAsync(ctx, fake, asHost));
        await app.StartAsync();
        server = new TlsServer(app, new Uri(app.Urls.First()).Port);
        return server;
    }

    private async Task ForwardAsync(HttpContext ctx, HttpMessageHandler fake, string asHost)
    {
        Interlocked.Increment(ref _requests);
        var uri = new UriBuilder(Uri.UriSchemeHttps, asHost) { Path = ctx.Request.Path, Query = ctx.Request.QueryString.Value }.Uri;
        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), uri);
        using var body = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body.ToArray());
        }
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
            else if (!string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        using var invoker = new HttpMessageInvoker(fake, disposeHandler: false);
        using var response = await invoker.SendAsync(request, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } type)
        {
            ctx.Response.ContentType = type.ToString();
        }
        await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
