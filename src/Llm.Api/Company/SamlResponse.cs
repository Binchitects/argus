using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Llm.Api.Company;

/// <summary>What an answer must be for: the provider that signs it, this app (audience and ACS), the request it answers, and now.</summary>
public sealed record SamlCheck(SamlProvider Provider, string SpEntityId, string AcsUrl, string? RequestId, bool AllowUnsolicited, DateTimeOffset Now);

/// <summary>An assertion that passed every check: its ID and how long to remember it (no replay), and the person it names.</summary>
public sealed record SamlAssertion(string Id, DateTimeOffset KeepUntil, CompanyPerson Person);

/// <summary>A signature that matches none of the provider's certificates: perhaps it rolled over to a new one.</summary>
public sealed class SamlKeyMismatchException(string message) : Exception(message);

/// <summary>
/// Reads a SAML Response posted to the ACS and checks it as SAML 2.0's Web Browser
/// SSO profile asks: an XML signature on the Response or the Assertion by the
/// provider's certificate only (never a key the message carries), covering by its ID
/// the very element read (no signature wrapping: exactly one assertion, no duplicate
/// IDs), RSA with SHA-256 or better; the issuer, the audience, the destination and
/// recipient, InResponseTo, the lifetime with a small clock skew, and a bearer
/// subject confirmation. Encrypted assertions are refused: the app has no key.
/// </summary>
public static class SamlResponse
{
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    private const string ProtocolNs = SamlIdp.ProtocolNs;
    private const string AssertionNs = SamlIdp.AssertionNs;
    private const string Success = "urn:oasis:names:tc:SAML:2.0:status:Success";
    private const string Bearer = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
    // SHA-1 is out: a provider that still signs with it is set to SHA-256 (all of them can).
    private static readonly HashSet<string> SignatureMethods = [SignedXml.XmlDsigRSASHA256Url, SignedXml.XmlDsigRSASHA384Url, SignedXml.XmlDsigRSASHA512Url];
    private static readonly HashSet<string> DigestMethods = [SignedXml.XmlDsigSHA256Url, SignedXml.XmlDsigSHA384Url, SignedXml.XmlDsigSHA512Url];
    private static readonly HashSet<string> Canonicalizations =
    [
        SignedXml.XmlDsigExcC14NTransformUrl, SignedXml.XmlDsigExcC14NWithCommentsTransformUrl,
        SignedXml.XmlDsigC14NTransformUrl, SignedXml.XmlDsigC14NWithCommentsTransformUrl,
    ];

    /// <summary>The answer's assertion and the person it names, or a <see cref="CompanyIdpException"/> saying what is wrong with it.</summary>
    public static SamlAssertion Read(string samlResponse, SamlCheck c, CompanySignInOptions o)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(samlResponse.Trim());
        }
        catch (FormatException)
        {
            throw Fail("the answer is not base64");
        }
        var doc = LoadXml(bytes, "the answer is not XML");
        var response = doc.DocumentElement;
        if (response is not { LocalName: "Response", NamespaceURI: ProtocolNs })
        {
            throw Fail("the answer is not a SAML response");
        }
        // A refusal (no access, a cancelled sign-in) says why; it signs nobody in, signed or not.
        var status = Child(response, ProtocolNs, "Status");
        var code = status is null ? null : Child(status, ProtocolNs, "StatusCode");
        if (code?.GetAttribute("Value") != Success)
        {
            var detail = code is null ? null : Child(code, ProtocolNs, "StatusCode")?.GetAttribute("Value");
            var message = status is null ? null : Text(Child(status, ProtocolNs, "StatusMessage"));
            throw Fail($"the identity provider answered {Last(detail ?? code?.GetAttribute("Value")) ?? "no status"}{(string.IsNullOrWhiteSpace(message) ? "" : ": " + message)}");
        }
        if (doc.GetElementsByTagName("EncryptedAssertion", AssertionNs).Count > 0)
        {
            throw Fail("the assertion is encrypted, and the app has no key to decrypt it: turn assertion encryption off for this app at the identity provider");
        }
        // Exactly one assertion, right in the response: a second one hidden elsewhere is how signatures get wrapped around forged content.
        var assertions = doc.GetElementsByTagName("Assertion", AssertionNs);
        if (assertions.Count != 1 || assertions[0]!.ParentNode != response)
        {
            throw Fail("the response does not carry exactly one assertion");
        }
        var assertion = (XmlElement)assertions[0]!;
        var responseSigned = Verify(doc, response, c.Provider.Certificates);
        var assertionSigned = Verify(doc, assertion, c.Provider.Certificates);
        if (!responseSigned && !assertionSigned)
        {
            throw Fail("the response is not signed");
        }

        var issuer = Text(Child(assertion, AssertionNs, "Issuer"));
        if (issuer != c.Provider.EntityId)
        {
            throw Fail($"the assertion is from {issuer ?? "no issuer"}, not {c.Provider.EntityId}");
        }
        if (Child(response, AssertionNs, "Issuer") is { } responseIssuer && Text(responseIssuer) != c.Provider.EntityId)
        {
            throw Fail($"the response is from {Text(responseIssuer)}, not {c.Provider.EntityId}");
        }
        if (response.GetAttribute("Destination") is { Length: > 0 } destination && destination != c.AcsUrl)
        {
            throw Fail($"the response is for {destination}, not {c.AcsUrl}");
        }
        Answers(c, response.GetAttribute("InResponseTo"));

        var id = assertion.GetAttribute("ID");
        if (id.Length is 0 or > 256)
        {
            throw Fail("the assertion has no usable ID");
        }
        var before = c.Now - Skew;
        var after = c.Now + Skew;
        var conditions = Child(assertion, AssertionNs, "Conditions") ?? throw Fail("the assertion has no conditions (audience and lifetime)");
        if (Time(conditions, "NotBefore") is { } notBefore && notBefore > after)
        {
            throw Fail("the assertion is not valid yet (check the clocks)");
        }
        var notOnOrAfter = Time(conditions, "NotOnOrAfter");
        if (notOnOrAfter <= before)
        {
            throw Fail("the assertion is out of date (check the clocks)");
        }
        // Each audience restriction must name this app.
        var restrictions = SamlIdp.Children(conditions, AssertionNs, "AudienceRestriction").ToList();
        if (restrictions.Count == 0 || restrictions.Any(r => !SamlIdp.Children(r, AssertionNs, "Audience").Any(a => Text(a) == c.SpEntityId)))
        {
            var audiences = restrictions.SelectMany(r => SamlIdp.Children(r, AssertionNs, "Audience")).Select(Text).ToList();
            throw Fail($"the assertion is for another service provider ({(audiences.Count == 0 ? "no audience" : string.Join(", ", audiences))}), not {c.SpEntityId}");
        }

        var subject = Child(assertion, AssertionNs, "Subject") ?? throw Fail("the assertion names nobody (no subject)");
        if (Child(subject, AssertionNs, "EncryptedID") is not null)
        {
            throw Fail("the NameID is encrypted, and the app has no key to decrypt it: turn NameID encryption off at the identity provider");
        }
        var nameId = Text(Child(subject, AssertionNs, "NameID"));
        if (string.IsNullOrWhiteSpace(nameId))
        {
            throw Fail("the assertion names nobody (no NameID)");
        }
        var confirmedUntil = Confirm(c, subject, before, after);

        var attributes = SamlIdp.Children(assertion, AssertionNs, "AttributeStatement").SelectMany(s => SamlIdp.Children(s, AssertionNs, "Attribute")).ToList();
        IReadOnlyList<string> Values(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return [];
            }
            name = name.Trim();
            // Its Name (often a URI), or else its FriendlyName (Keycloak's "email" for urn:oid:1.2.840.113549.1.9.1).
            var found = attributes.Where(a => string.Equals(a.GetAttribute("Name"), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0)
            {
                found = [.. attributes.Where(a => string.Equals(a.GetAttribute("FriendlyName"), name, StringComparison.OrdinalIgnoreCase))];
            }
            return [.. found.SelectMany(a => SamlIdp.Children(a, AssertionNs, "AttributeValue")).Select(v => v.InnerText.Trim()).Where(v => v.Length > 0)];
        }
        string? Value(string? name) => Values(name) is { Count: > 0 } values ? values[0] : null;
        var person = new CompanyPerson(
            Subject(nameId),
            string.IsNullOrWhiteSpace(o.SamlUserNameAttribute) ? nameId : Value(o.SamlUserNameAttribute),
            Value(o.SamlEmailAttribute),
            null,
            Value(o.SamlDisplayNameAttribute),
            [.. Values(o.SamlGroupsAttribute).Distinct(StringComparer.OrdinalIgnoreCase)],
            Saml: true);
        var keepUntil = (notOnOrAfter is { } end && end > confirmedUntil ? end : confirmedUntil) + Skew;
        return new SamlAssertion(id, keepUntil, person);
    }

    /// <summary>XML as the app reads what others send: no DTD, nothing fetched, whitespace kept (signatures cover it), a size limit.</summary>
    public static XmlDocument LoadXml(byte[] bytes, string notXml)
    {
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 5_000_000 };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            doc.Load(reader);
        }
        catch (XmlException)
        {
            throw Fail(notXml);
        }
        return doc;
    }

    /// <summary>
    /// Checks the signature directly in this element, if there is one: it must cover this
    /// element by its ID and nothing else, with the provider's certificates. False when unsigned.
    /// </summary>
    private static bool Verify(XmlDocument doc, XmlElement element, IReadOnlyList<X509Certificate2> certificates)
    {
        var signatures = element.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Signature" && e.NamespaceURI == SignedXml.XmlDsigNamespaceUrl).ToList();
        if (signatures.Count == 0)
        {
            return false;
        }
        var what = element.LocalName.ToLowerInvariant();
        if (signatures.Count > 1)
        {
            throw Fail($"the {what} carries more than one signature");
        }
        var id = element.GetAttribute("ID");
        if (id.Length == 0)
        {
            throw Fail($"the signed {what} has no ID");
        }
        // The signature finds what it covers by ID: another element with the same ID could be what it covers instead.
        var sameId = doc.GetElementsByTagName("*").OfType<XmlElement>().Count(e => e.GetAttribute("ID") == id || e.GetAttribute("Id") == id || e.GetAttribute("id") == id);
        if (sameId != 1)
        {
            throw Fail($"{sameId} elements carry the {what}'s ID");
        }
        var signed = new SignedXml(element);
        try
        {
            signed.LoadXml(signatures[0]);
        }
        catch (CryptographicException)
        {
            throw Fail($"the {what}'s signature cannot be read");
        }
        var info = signed.SignedInfo!;
        if (info.References.Count != 1 || info.References[0] is not Reference reference || reference.Uri != "#" + id)
        {
            throw Fail($"the signature in the {what} does not cover the {what}");
        }
        if (!SignatureMethods.Contains(info.SignatureMethod ?? ""))
        {
            throw Fail($"the {what} is signed with {Last(info.SignatureMethod) ?? "no algorithm"}: set the identity provider to sign with RSA-SHA256");
        }
        if (!DigestMethods.Contains(reference.DigestMethod ?? ""))
        {
            throw Fail($"the {what}'s signature digests with {Last(reference.DigestMethod) ?? "nothing"}: set the identity provider to SHA-256");
        }
        var transforms = reference.TransformChain;
        if (!Canonicalizations.Contains(info.CanonicalizationMethod)
            || Enumerable.Range(0, transforms.Count).Select(i => transforms[i].Algorithm ?? "")
                .Any(t => t != SignedXml.XmlDsigEnvelopedSignatureTransformUrl && !Canonicalizations.Contains(t)))
        {
            throw Fail($"the {what}'s signature uses a transform the app does not accept");
        }
        XmlElement? covered;
        try
        {
            covered = signed.GetIdElement(doc, id);
        }
        catch (CryptographicException)
        {
            covered = null;
        }
        if (!ReferenceEquals(covered, element))
        {
            throw Fail($"the signature covers another element than the {what} it is in");
        }
        // Only the provider's certificates: a key or certificate inside the message proves nothing.
        if (!certificates.Any(cert => Matches(signed, cert)))
        {
            throw new SamlKeyMismatchException($"the {what}'s signature does not match the identity provider's certificate");
        }
        return true;
    }

    private static bool Matches(SignedXml signed, X509Certificate2 certificate)
    {
        try
        {
            using var key = certificate.GetRSAPublicKey();
            return key is not null && signed.CheckSignature(key);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The bearer confirmation for this app: to the ACS, answering this request, not expired. Until when it holds.</summary>
    private static DateTimeOffset Confirm(SamlCheck c, XmlElement subject, DateTimeOffset before, DateTimeOffset after)
    {
        var why = "is missing (no bearer subject confirmation)";
        foreach (var confirmation in SamlIdp.Children(subject, AssertionNs, "SubjectConfirmation").Where(s => s.GetAttribute("Method") == Bearer))
        {
            var data = Child(confirmation, AssertionNs, "SubjectConfirmationData");
            if (data is null || Time(data, "NotOnOrAfter") is not { } until)
            {
                why = "has no end (NotOnOrAfter)";
                continue;
            }
            if (until <= before)
            {
                throw Fail("the assertion is out of date (check the clocks)");
            }
            if (Time(data, "NotBefore") is { } notBefore && notBefore > after)
            {
                throw Fail("the assertion is not valid yet (check the clocks)");
            }
            var recipient = data.GetAttribute("Recipient");
            if (recipient != c.AcsUrl)
            {
                why = $"is for {(recipient.Length == 0 ? "no recipient" : recipient)}, not {c.AcsUrl}";
                continue;
            }
            Answers(c, data.GetAttribute("InResponseTo"));
            return until;
        }
        throw Fail("the assertion's subject confirmation " + why);
    }

    /// <summary>An InResponseTo must be this browser's request; none is an answer nobody asked for, taken only when an admin allows it.</summary>
    private static void Answers(SamlCheck c, string inResponseTo)
    {
        if (inResponseTo.Length == 0)
        {
            if (!c.AllowUnsolicited)
            {
                throw Fail("the response answers no sign-in request of the app (sign-in started at the identity provider is off)");
            }
            return;
        }
        if (c.RequestId is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(inResponseTo), Encoding.UTF8.GetBytes(c.RequestId)))
        {
            throw Fail("the response answers a sign-in request this browser did not make (or it took over 10 minutes)");
        }
    }

    /// <summary>The person's subject here: "saml:" and the NameID (its SHA-256 when too long to keep).</summary>
    private static string Subject(string nameId)
    {
        var subject = "saml:" + nameId.Trim();
        return subject.Length <= 255 ? subject : "saml:sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nameId.Trim())));
    }

    private static DateTimeOffset? Time(XmlElement e, string attribute)
    {
        var value = e.GetAttribute(attribute);
        if (value.Length == 0)
        {
            return null;
        }
        try
        {
            return XmlConvert.ToDateTimeOffset(value).ToUniversalTime();
        }
        catch (FormatException)
        {
            throw Fail($"{attribute} in the assertion is not a time ({value})");
        }
    }

    private static XmlElement? Child(XmlElement parent, string ns, string name) => SamlIdp.Children(parent, ns, name).FirstOrDefault();

    /// <summary>All the element's text, comments left out: what its signature covers (no "ada@example.test&lt;!----&gt;.evil.test" tricks).</summary>
    private static string? Text(XmlElement? e) => e?.InnerText.Trim();

    /// <summary>The last part of a URN or URL, for people: Responder, AuthnFailed, rsa-sha1.</summary>
    private static string? Last(string? uri) => string.IsNullOrEmpty(uri) ? null : uri[(uri.LastIndexOfAny([':', '#', '/']) + 1)..];

    private static CompanyIdpException Fail(string message) => new(message);
}
