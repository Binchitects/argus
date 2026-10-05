using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;

namespace Llm.Api.Company;

/// <summary>The SAML identity provider as the app trusts it: its entity ID, its sign-in address and its signing certificates.</summary>
public sealed record SamlProvider(string EntityId, string SsoUrl, IReadOnlyList<X509Certificate2> Certificates, bool WantsSignedRequests, bool FromUrl);

/// <summary>What an identity provider's metadata says, before the values set by hand are put over it.</summary>
public sealed record SamlIdpMetadata(string EntityId, string? SsoUrl, IReadOnlyList<X509Certificate2> Certificates, bool WantsSignedRequests);

/// <summary>
/// The company's SAML 2.0 identity provider: its metadata (read from its address and
/// kept for an hour, or pasted) under the values an admin set by hand, the sign-in
/// request (an unsigned AuthnRequest by the HTTP-Redirect binding), and its answers
/// read by <see cref="SamlResponse"/>. When a signature matches none of its
/// certificates and they came from its address, the metadata is read again (it
/// rolled over to a new certificate), at most once a minute.
/// </summary>
public sealed class SamlIdp(IHttpClientFactory http, TimeProvider clock) : IDisposable
{
    public const string ProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";
    public const string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";
    public const string MetadataNs = "urn:oasis:names:tc:SAML:2.0:metadata";
    public const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";
    public const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";
    private const string DsNs = "http://www.w3.org/2000/09/xmldsig#";
    private const string UnspecifiedNameId = "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified";

    /// <summary>The NameID formats this app takes, in the order its metadata offers them.</summary>
    public static readonly string[] NameIdFormats =
    [
        "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress",
        "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent",
        UnspecifiedNameId,
    ];

    private static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    private sealed record Cached(string Key, SamlProvider Provider, DateTimeOffset At);

    private Cached? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>This app's entity ID at the provider: the setting, or https://DOMAIN.</summary>
    public static string EntityId(CompanySignInOptions o, string origin) => string.IsNullOrWhiteSpace(o.SamlEntityId) ? origin : o.SamlEntityId.Trim();

    /// <summary>The provider as these settings describe it, kept for an hour (a change of the settings reads it again).</summary>
    public async Task<SamlProvider> ProviderAsync(CompanySignInOptions o, bool fresh = false, CancellationToken ct = default)
    {
        var key = string.Join('\n', o.SamlMetadataUrl, o.SamlMetadata, o.SamlSsoUrl, o.SamlIdpEntityId, o.SamlCertificate);
        var now = clock.GetUtcNow();
        var c = _cached;
        if (c is not null && c.Key == key && now - c.At < Keep && !fresh)
        {
            return c.Provider;
        }
        await _gate.WaitAsync(ct);
        try
        {
            c = _cached;
            // A signature that matches no certificate reads the metadata again, but at most once a minute.
            if (c is not null && c.Key == key && now - c.At < (fresh ? TimeSpan.FromMinutes(1) : Keep))
            {
                return c.Provider;
            }
            var p = await ResolveAsync(o, ct);
            _cached = new Cached(key, p, now);
            return p;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Checks the answer the browser posted (SAMLResponse) and returns its assertion.
    /// A signature that matches none of the certificates of metadata read from an
    /// address reads it again once, for a provider that rolled over to a new certificate.
    /// </summary>
    public async Task<SamlAssertion> ReadAsync(CompanySignInOptions o, string samlResponse, string spEntityId, string acsUrl, string? requestId, CancellationToken ct = default)
    {
        var p = await ProviderAsync(o, ct: ct);
        SamlCheck Check(SamlProvider provider) => new(provider, spEntityId, acsUrl, requestId, o.SamlAllowIdpInitiated, clock.GetUtcNow());
        try
        {
            return SamlResponse.Read(samlResponse, Check(p), o);
        }
        catch (SamlKeyMismatchException) when (p.FromUrl)
        {
            var again = await ProviderAsync(o, fresh: true, ct);
            if (ReferenceEquals(again, p))
            {
                throw;
            }
            return SamlResponse.Read(samlResponse, Check(again), o);
        }
    }

    /// <summary>
    /// Where to send the browser: the provider's sign-in address with an AuthnRequest
    /// (deflated, base64, unsigned) and the RelayState. The request's ID comes back as
    /// the answer's InResponseTo.
    /// </summary>
    public static (string Id, string Url) SignInRequest(SamlProvider p, string spEntityId, string acsUrl, string relayState, DateTimeOffset now)
    {
        // An xs:ID must not start with a digit.
        var id = "_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        XNamespace samlp = ProtocolNs;
        XNamespace saml = AssertionNs;
        var request = new XElement(samlp + "AuthnRequest",
            new XAttribute(XNamespace.Xmlns + "samlp", ProtocolNs),
            new XAttribute(XNamespace.Xmlns + "saml", AssertionNs),
            new XAttribute("ID", id),
            new XAttribute("Version", "2.0"),
            new XAttribute("IssueInstant", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            new XAttribute("Destination", p.SsoUrl),
            new XAttribute("AssertionConsumerServiceURL", acsUrl),
            new XAttribute("ProtocolBinding", PostBinding),
            new XElement(saml + "Issuer", spEntityId),
            // Unspecified: the NameID the provider is set up to send; asking for one it is not set up for fails at Okta and Entra ID.
            new XElement(samlp + "NameIDPolicy", new XAttribute("Format", UnspecifiedNameId), new XAttribute("AllowCreate", "true")));
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(request.ToString(SaveOptions.DisableFormatting)));
        }
        var url = QueryHelpers.AddQueryString(p.SsoUrl, new Dictionary<string, string?>
        {
            ["SAMLRequest"] = Convert.ToBase64String(buffer.ToArray()),
            ["RelayState"] = relayState,
        });
        return (id, url);
    }

    /// <summary>This app as a service provider, for the identity provider: its entity ID, where answers go (HTTP-POST) and the NameID formats it takes.</summary>
    public static string Metadata(string spEntityId, string acsUrl)
    {
        XNamespace md = MetadataNs;
        var doc = new XElement(md + "EntityDescriptor",
            new XAttribute(XNamespace.Xmlns + "md", MetadataNs),
            new XAttribute("entityID", spEntityId),
            new XElement(md + "SPSSODescriptor",
                new XAttribute("AuthnRequestsSigned", "false"),
                new XAttribute("WantAssertionsSigned", "true"),
                new XAttribute("protocolSupportEnumeration", ProtocolNs),
                NameIdFormats.Select(f => new XElement(md + "NameIDFormat", f)),
                new XElement(md + "AssertionConsumerService",
                    new XAttribute("Binding", PostBinding),
                    new XAttribute("Location", acsUrl),
                    new XAttribute("index", "0"),
                    new XAttribute("isDefault", "true"))));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + doc;
    }

    /// <summary>Reads the provider of these settings (saved or not), in words: what an admin needs before saving them.</summary>
    public async Task<CompanyTestResult> TestAsync(CompanySignInOptions o, CancellationToken ct = default)
    {
        if (!o.SamlEnabled)
        {
            return new(false, "Set the identity provider's metadata (its address, or the XML), or its sign-in address, entity ID and certificate.");
        }
        try
        {
            var p = await ResolveAsync(o, ct);
            var n = p.Certificates.Count;
            var until = p.Certificates.Max(c => c.NotAfter.ToUniversalTime());
            var text = new StringBuilder($"Found {p.EntityId}: people sign in at {p.SsoUrl}, and it signs with {n} {(n == 1 ? "certificate" : "certificates")}, ");
            text.Append(until < clock.GetUtcNow().UtcDateTime
                ? $"expired on {until:yyyy-MM-dd} (the app still checks signatures with it; renew it at the provider)."
                : $"valid until {until:yyyy-MM-dd}.");
            if (p.WantsSignedRequests)
            {
                text.Append(" Its metadata asks for signed sign-in requests, and the app sends them unsigned: if sign-in fails, turn that requirement off for this app at the provider.");
            }
            return new(true, text.ToString());
        }
        catch (CompanyIdpException ex)
        {
            return new(false, char.ToUpperInvariant(ex.Message[0]) + ex.Message[1..] + ".");
        }
    }

    /// <summary>The metadata (its address first, then the pasted XML) with the values set by hand over it.</summary>
    private async Task<SamlProvider> ResolveAsync(CompanySignInOptions o, CancellationToken ct)
    {
        var byHandEntity = Trimmed(o.SamlIdpEntityId);
        SamlIdpMetadata? m = null;
        var fromUrl = Trimmed(o.SamlMetadataUrl) is not null;
        if (Trimmed(o.SamlMetadataUrl) is { } url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            {
                throw new CompanyIdpException($"the metadata address {url} is not an https:// (or http://) address");
            }
            m = ParseMetadata(await GetAsync(u, ct), byHandEntity);
        }
        else if (Trimmed(o.SamlMetadata) is { } xml)
        {
            m = ParseMetadata(xml, byHandEntity);
        }
        var entityId = byHandEntity ?? m?.EntityId
            ?? throw new CompanyIdpException("the identity provider's entity ID is not known: set its metadata, or its entity ID by hand");
        var sso = Trimmed(o.SamlSsoUrl) ?? m?.SsoUrl
            ?? throw new CompanyIdpException(m is null
                ? "the identity provider's sign-in address is not known: set its metadata, or its sign-in address by hand"
                : "the identity provider's metadata offers no sign-in by the HTTP-Redirect binding: set its sign-in address by hand");
        if (!Uri.TryCreate(sso, UriKind.Absolute, out var ssoUri) || (ssoUri.Scheme != Uri.UriSchemeHttps && ssoUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new CompanyIdpException($"the sign-in address {sso} is not an https:// (or http://) address");
        }
        var certificates = Trimmed(o.SamlCertificate) is { } pem ? Certificates(pem) : m?.Certificates ?? [];
        if (certificates.Count == 0)
        {
            throw new CompanyIdpException("the identity provider's signing certificate is not known: set its metadata, or its certificate by hand");
        }
        return new SamlProvider(entityId, sso, certificates, m?.WantsSignedRequests == true, fromUrl && Trimmed(o.SamlCertificate) is null);
    }

    /// <summary>
    /// An identity provider's SAML metadata: an EntityDescriptor, or an EntitiesDescriptor
    /// holding one identity provider (or the one with this entity ID). Its HTTP-Redirect
    /// sign-in address and its signing certificates (several while it rolls over to a new one).
    /// </summary>
    public static SamlIdpMetadata ParseMetadata(string xml, string? entityId = null)
    {
        var doc = SamlResponse.LoadXml(Encoding.UTF8.GetBytes(xml.Trim()), "the identity provider's metadata is not XML");
        var root = doc.DocumentElement;
        if (root is null || root.NamespaceURI != MetadataNs || (root.LocalName != "EntityDescriptor" && root.LocalName != "EntitiesDescriptor"))
        {
            throw new CompanyIdpException("the identity provider's metadata is not SAML metadata (no EntityDescriptor)");
        }
        var providers = doc.GetElementsByTagName("EntityDescriptor", MetadataNs).OfType<XmlElement>()
            .Select(e => (Entity: e, Idp: Children(e, MetadataNs, "IDPSSODescriptor")
                .FirstOrDefault(d => d.GetAttribute("protocolSupportEnumeration").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ProtocolNs))))
            .Where(x => x.Idp is not null)
            .ToList();
        if (entityId is not null)
        {
            providers = [.. providers.Where(x => x.Entity.GetAttribute("entityID") == entityId)];
        }
        if (providers.Count != 1)
        {
            throw new CompanyIdpException(providers.Count == 0
                ? entityId is null ? "the metadata describes no SAML 2.0 identity provider" : $"the metadata describes no identity provider {entityId}"
                : $"the metadata describes {providers.Count} identity providers: set the entity ID by hand to pick one");
        }
        var (entity, idp) = providers[0];
        var id = entity.GetAttribute("entityID").Trim();
        if (id.Length == 0)
        {
            throw new CompanyIdpException("the metadata names no entityID");
        }
        var sso = Children(idp!, MetadataNs, "SingleSignOnService").FirstOrDefault(s => s.GetAttribute("Binding") == RedirectBinding)?.GetAttribute("Location").Trim();
        var certificates = new List<X509Certificate2>();
        foreach (var key in Children(idp!, MetadataNs, "KeyDescriptor").Where(k => k.GetAttribute("use") is "" or "signing"))
        {
            foreach (var text in key.GetElementsByTagName("X509Certificate", DsNs).OfType<XmlElement>())
            {
                certificates.AddRange(Certificates(text.InnerText));
            }
        }
        if (certificates.Count == 0)
        {
            throw new CompanyIdpException("the metadata lists no signing certificate");
        }
        var wants = idp!.GetAttribute("WantAuthnRequestsSigned") is "true" or "1";
        return new SamlIdpMetadata(id, string.IsNullOrEmpty(sso) ? null : sso, certificates, wants);
    }

    /// <summary>Certificates from PEM (one or more blocks) or the base64 of one, as metadata and the Settings page give them.</summary>
    public static IReadOnlyList<X509Certificate2> Certificates(string text)
    {
        try
        {
            if (text.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                var pem = new X509Certificate2Collection();
                pem.ImportFromPem(text);
                return pem.Count == 0 ? throw new CryptographicException("no certificate") : [.. pem];
            }
            return [X509CertificateLoader.LoadCertificate(Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))))];
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new CompanyIdpException("the signing certificate is not a certificate (PEM, or its base64)", inner: ex);
        }
    }

    private async Task<string> GetAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var res = await http.CreateClient(CompanyIdp.Client).GetAsync(url, ct);
            return res.IsSuccessStatusCode
                ? await res.Content.ReadAsStringAsync(ct)
                : throw new CompanyIdpException($"the metadata at {url} answered HTTP {(int)res.StatusCode}", unavailable: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CompanyIdpException($"the metadata at {url} cannot be reached: {ex.Message}", unavailable: true, ex);
        }
    }

    internal static IEnumerable<XmlElement> Children(XmlElement parent, string ns, string name) =>
        parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == name && e.NamespaceURI == ns);

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose() => _gate.Dispose();
}
