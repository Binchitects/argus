using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace Llm.Api.Identity;

/// <summary>
/// Encrypts the Data Protection key ring before it is stored in the database,
/// with AES-256-GCM under a key derived from APP_DATA_KEY (.env). Those keys
/// sign sessions and protect the OIDC signing key, so without this a database
/// dump alone would be enough to forge a session.
/// </summary>
public sealed class KeyRingEncryptor(byte[] key) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        var element = new XElement("encryptedKey",
            new XAttribute("alg", "A256GCM"),
            new XElement("value", Convert.ToBase64String([.. nonce, .. tag, .. cipher])));
        return new EncryptedXmlInfo(element, typeof(KeyRingDecryptor));
    }

    /// <summary>A purpose-bound key: APP_DATA_KEY itself is never used directly.</summary>
    public static byte[] Derive(string secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: Encoding.UTF8.GetBytes("llm-app data protection key ring v1"));
}

/// <summary>Found by type name in the stored XML, and built by Data Protection with the service provider.</summary>
public sealed class KeyRingDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var secret = services.GetRequiredService<IOptions<AuthOptions>>().Value.DataKey;
        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException("The key ring is encrypted but APP_DATA_KEY is not set. Restore it in .env.");
        }
        var blob = Convert.FromBase64String(encryptedElement.Element("value")!.Value);
        var plain = new byte[blob.Length - 28];
        try
        {
            using var aes = new AesGcm(KeyRingEncryptor.Derive(secret), 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException("APP_DATA_KEY does not match the key the key ring was encrypted with. It must never change after the first start.", ex);
        }
        return XElement.Parse(Encoding.UTF8.GetString(plain));
    }
}
