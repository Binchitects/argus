using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;

namespace Llm.Tests;

/// <summary>
/// A SAML 2.0 identity provider at https://saml-idp.test: its metadata, and
/// <see cref="Approve"/> playing the person signing in there, which checks the app's
/// AuthnRequest and returns the signed Response the browser then posts to the ACS.
/// Its certificate is made for each run; <see cref="Forgery"/> breaks the answer the ways attackers do.
/// </summary>
public sealed class FakeSamlIdp : HttpMessageHandler
{
    public const string EntityId = "https://saml-idp.test/realms/staff";
    public const string MetadataUrl = "https://saml-idp.test/metadata";
    public const string SsoUrl = "https://saml-idp.test/sso";
    public const string Acs = $"https://{AppFixture.Domain}/api/auth/company/saml/acs";
    public const string SpEntityId = $"https://{AppFixture.Domain}";
    private const string Samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Saml = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <summary>A person at the provider: their NameID and the attributes it sends.</summary>
    public sealed record Person(string NameId, string? Email, string[] Groups, string? Name = null);

    /// <summary>
    /// What a forged or broken answer gets wrong. Wrap: "wrapped" puts a forged assertion (for Forged) where the
    /// genuine one was and hides that in Extensions; "signature" also gives the forged one the genuine one's
    /// signature; "same-id" gives it the genuine one's ID. Comment splits the NameID after signing
    /// (ada@example.test.evil.test as ada@example.test, a comment, .evil.test), which the signature does not see.
    /// </summary>
    public sealed record Forgery(
        bool NoSignature = false,
        bool OtherKey = false,
        bool SignResponse = false,
        string? Audience = null,
        string? Issuer = null,
        string? Recipient = null,
        bool Expired = false,
        bool Encrypted = false,
        bool Sha1 = false,
        bool Denied = false,
        string? Wrap = null,
        Person? Forged = null,
        int? Comment = null);

    private readonly X509Certificate2 _certificate = Certificate("CN=saml-idp.test");
    // The attacker's own certificate: a signature by it, carried with it in KeyInfo, must not pass.
    private readonly X509Certificate2 _other = Certificate("CN=saml-idp.test");

    public bool Down { get; set; }

    /// <summary>The provider's signing certificate, base64 (as metadata carries it).</summary>
    public string CertificateBase64 => Convert.ToBase64String(_certificate.Export(X509ContentType.Cert));

    public string CertificatePem => _certificate.ExportCertificatePem();

    public string Metadata(string? sso = SsoUrl) =>
        $"""
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" xmlns:ds="http://www.w3.org/2000/09/xmldsig#" entityID="{EntityId}">
          <md:IDPSSODescriptor WantAuthnRequestsSigned="false" protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
            <md:KeyDescriptor use="encryption"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>{Convert.ToBase64String(_other.Export(X509ContentType.Cert))}</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
            <md:KeyDescriptor use="signing"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>
              {CertificateBase64}
            </ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>
            <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>
            <md:SingleSignOnService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="https://saml-idp.test/sso-post"/>
            {(sso is null ? "" : $"<md:SingleSignOnService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect\" Location=\"{sso}\"/>")}
          </md:IDPSSODescriptor>
        </md:EntityDescriptor>
        """;

    /// <summary>The AuthnRequest the app sent (checked as an identity provider would), its ID and the RelayState.</summary>
    public static (XmlElement Request, string Id, string RelayState) ReadRequest(Uri signIn)
    {
        Assert.Equal(SsoUrl, signIn.GetLeftPart(UriPartial.Path));
        var q = QueryHelpers.ParseQuery(signIn.Query);
        Assert.False(q.ContainsKey("Signature")); // unsigned
        using var inflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(q["SAMLRequest"].ToString())), CompressionMode.Decompress);
        var doc = new XmlDocument();
        doc.Load(inflate);
        var request = doc.DocumentElement!;
        Assert.Equal("AuthnRequest", request.LocalName);
        Assert.Equal(Samlp, request.NamespaceURI);
        Assert.Equal("2.0", request.GetAttribute("Version"));
        Assert.Equal(SsoUrl, request.GetAttribute("Destination"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", request.GetAttribute("ProtocolBinding"));
        return (request, request.GetAttribute("ID"), q["RelayState"].ToString());
    }

    /// <summary>The person signs in at the provider: the answer the browser posts to the ACS (SAMLResponse, RelayState).</summary>
    public (string SamlResponse, string RelayState) Approve(Uri signIn, Person person, Forgery? forgery = null, string sp = SpEntityId)
    {
        var (request, id, relayState) = ReadRequest(signIn);
        Assert.Equal(Acs, request.GetAttribute("AssertionConsumerServiceURL"));
        Assert.Equal(sp, request.GetElementsByTagName("Issuer", Saml)[0]!.InnerText);
        Assert.False(string.IsNullOrEmpty(relayState));
        return (Respond(person, id, forgery, sp), relayState);
    }

    /// <summary>A signed Response to the request of this ID (null: one nobody asked for, as from the provider's portal).</summary>
    public string Respond(Person person, string? inResponseTo, Forgery? forgery = null, string sp = SpEntityId)
    {
        var f = forgery ?? new Forgery();
        var doc = Document(Response(person, inResponseTo, f, sp, "_" + Guid.NewGuid().ToString("N")));
        var response = doc.DocumentElement!;
        var genuine = (XmlElement)doc.GetElementsByTagName("Assertion", Saml)[0]!;
        if (f.Encrypted)
        {
            // What an encrypting provider sends instead of the assertion (the content does not matter: it is refused unread).
            var encrypted = doc.CreateElement("saml", "EncryptedAssertion", Saml);
            encrypted.InnerXml = "<xenc:EncryptedData xmlns:xenc=\"http://www.w3.org/2001/04/xmlenc#\"><xenc:CipherData><xenc:CipherValue>AAAA</xenc:CipherValue></xenc:CipherData></xenc:EncryptedData>";
            response.ReplaceChild(encrypted, genuine);
            Sign(response, _certificate);
            return Encode(doc);
        }
        if (f.NoSignature)
        {
            return Encode(doc);
        }
        if (f.SignResponse)
        {
            Sign(response, f.OtherKey ? _other : _certificate, f.Sha1);
            return Encode(doc);
        }
        Sign(genuine, f.OtherKey ? _other : _certificate, f.Sha1);
        if (f.Comment is { } at)
        {
            // After signing: the signed text stays the same (canonical XML leaves comments out), its first text node does not.
            var nameId = (XmlElement)genuine.GetElementsByTagName("NameID", Saml)[0]!;
            var text = nameId.InnerText;
            nameId.InnerText = text[..at];
            nameId.AppendChild(doc.CreateComment(""));
            nameId.AppendChild(doc.CreateTextNode(text[at..]));
        }
        if (f.Wrap is { } wrap)
        {
            // The attacker's own genuine answer, hidden in Extensions, and a forged assertion for someone else in its place.
            var forged = (XmlElement)doc.ImportNode(Document(Response(f.Forged ?? person, inResponseTo, new Forgery(), sp, "_x")).GetElementsByTagName("Assertion", Saml)[0]!, true);
            forged.SetAttribute("ID", wrap == "same-id" ? genuine.GetAttribute("ID") : "_forged" + Guid.NewGuid().ToString("N"));
            if (wrap == "signature")
            {
                var signature = genuine.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!;
                forged.InsertAfter(signature.CloneNode(true), forged.FirstChild);
            }
            var extensions = doc.CreateElement("samlp", "Extensions", Samlp);
            response.ReplaceChild(forged, genuine);
            extensions.AppendChild(genuine);
            response.InsertAfter(extensions, response.FirstChild);
        }
        return Encode(doc);
    }

    private static XElement Response(Person p, string? inResponseTo, Forgery f, string sp, string responseId)
    {
        XNamespace samlp = Samlp;
        XNamespace saml = Saml;
        var now = DateTime.UtcNow;
        var notBefore = f.Expired ? now.AddHours(-2) : now.AddMinutes(-1);
        var until = f.Expired ? now.AddHours(-1) : now.AddMinutes(5);
        string T(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        object? Answering() => inResponseTo is null ? null : new XAttribute("InResponseTo", inResponseTo);
        var attributes = new List<XElement>();
        if (p.Email is not null)
        {
            attributes.Add(new XElement(saml + "Attribute", new XAttribute("Name", "email"), new XElement(saml + "AttributeValue", p.Email)));
        }
        if (p.Name is not null)
        {
            attributes.Add(new XElement(saml + "Attribute", new XAttribute("Name", "displayName"), new XElement(saml + "AttributeValue", p.Name)));
        }
        attributes.Add(new XElement(saml + "Attribute", new XAttribute("Name", "groups"), p.Groups.Select(g => new XElement(saml + "AttributeValue", g))));
        return new XElement(samlp + "Response",
            new XAttribute(XNamespace.Xmlns + "samlp", Samlp),
            new XAttribute(XNamespace.Xmlns + "saml", Saml),
            new XAttribute("ID", responseId),
            new XAttribute("Version", "2.0"),
            new XAttribute("IssueInstant", T(now)),
            new XAttribute("Destination", Acs),
            Answering(),
            new XElement(saml + "Issuer", f.Issuer ?? EntityId),
            f.Denied
                ? new XElement(samlp + "Status",
                    new XElement(samlp + "StatusCode", new XAttribute("Value", "urn:oasis:names:tc:SAML:2.0:status:Responder"),
                        new XElement(samlp + "StatusCode", new XAttribute("Value", "urn:oasis:names:tc:SAML:2.0:status:AuthnFailed"))),
                    new XElement(samlp + "StatusMessage", "The person cancelled"))
                : new XElement(samlp + "Status", new XElement(samlp + "StatusCode", new XAttribute("Value", "urn:oasis:names:tc:SAML:2.0:status:Success"))),
            new XElement(saml + "Assertion",
                new XAttribute("ID", "_" + Guid.NewGuid().ToString("N")),
                new XAttribute("Version", "2.0"),
                new XAttribute("IssueInstant", T(now)),
                new XElement(saml + "Issuer", f.Issuer ?? EntityId),
                new XElement(saml + "Subject",
                    new XElement(saml + "NameID", new XAttribute("Format", "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress"), p.NameId),
                    new XElement(saml + "SubjectConfirmation", new XAttribute("Method", "urn:oasis:names:tc:SAML:2.0:cm:bearer"),
                        new XElement(saml + "SubjectConfirmationData", Answering(), new XAttribute("NotOnOrAfter", T(until)), new XAttribute("Recipient", f.Recipient ?? Acs)))),
                new XElement(saml + "Conditions", new XAttribute("NotBefore", T(notBefore)), new XAttribute("NotOnOrAfter", T(until)),
                    new XElement(saml + "AudienceRestriction", new XElement(saml + "Audience", f.Audience ?? sp))),
                new XElement(saml + "AuthnStatement", new XAttribute("AuthnInstant", T(now)), new XAttribute("SessionIndex", "_session"),
                    new XElement(saml + "AuthnContext", new XElement(saml + "AuthnContextClassRef", "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport"))),
                new XElement(saml + "AttributeStatement", attributes)));
    }

    /// <summary>An enveloped signature right after the element's Issuer, as SAML's schema places it, with the signer's certificate in KeyInfo.</summary>
    private static void Sign(XmlElement element, X509Certificate2 certificate, bool sha1 = false)
    {
        var signed = new SignedXml(element) { SigningKey = certificate.GetRSAPrivateKey() };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = sha1 ? SignedXml.XmlDsigRSASHA1Url : SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference("#" + element.GetAttribute("ID")) { DigestMethod = sha1 ? SignedXml.XmlDsigSHA1Url : SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        var issuer = element.ChildNodes.OfType<XmlElement>().First(e => e.LocalName == "Issuer");
        element.InsertAfter(element.OwnerDocument.ImportNode(signed.GetXml(), true), issuer);
    }

    private static XmlDocument Document(XElement xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml.ToString(SaveOptions.DisableFormatting));
        return doc;
    }

    private static string Encode(XmlDocument doc) => Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.OuterXml));

    private static X509Certificate2 Certificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("Connection refused (saml-idp.test:443)");
        }
        return Task.FromResult(request.RequestUri!.ToString() == MetadataUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Metadata(), Encoding.UTF8, "application/samlmetadata+xml") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
