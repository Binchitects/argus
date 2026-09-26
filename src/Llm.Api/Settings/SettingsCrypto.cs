using System.Security.Cryptography;
using System.Text;

namespace Llm.Api.Settings;

/// <summary>
/// Secret setting values (the directory's service password) are stored
/// AES-256-GCM encrypted under a key derived from APP_DATA_KEY, so a database
/// dump alone does not reveal them. "enc:" marks an encrypted value.
/// </summary>
public static class SettingsCrypto
{
    public const string Prefix = "enc:";

    private static byte[] Key(string dataKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(dataKey), 32, info: Encoding.UTF8.GetBytes("llm-app settings v1"));

    public static string Encrypt(string plain, string dataKey)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[bytes.Length];
        using (var aes = new AesGcm(Key(dataKey), tag.Length))
        {
            aes.Encrypt(nonce, bytes, cipher, tag);
        }
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The plain value, or null when it cannot be decrypted (APP_DATA_KEY changed).</summary>
    public static string? Decrypt(string stored, string? dataKey)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }
        if (string.IsNullOrEmpty(dataKey))
        {
            return null;
        }
        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = new byte[blob.Length - 28];
            using var aes = new AesGcm(Key(dataKey), 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
