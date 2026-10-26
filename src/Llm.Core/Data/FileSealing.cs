using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Core.Data;

/// <summary>
/// People's files sealed at rest: each file's bytes encrypted with AES-256-GCM under a key from APP_KEY before they reach the
/// database, and opened as they are read. A file stored before (or by an app with no key) reads as it is, and is sealed when it
/// is next saved (the app's sealing job does them all).
/// </summary>
public sealed class FileSealer(byte[]? key)
{
    /// <summary>What a sealed file starts with: "\0ARENAF1", then the nonce, the tag, and the encrypted bytes.</summary>
    private static readonly byte[] Magic = [0x00, (byte)'A', (byte)'R', (byte)'E', (byte)'N', (byte)'A', (byte)'F', (byte)'1'];

    private const int NonceSize = 12, TagSize = 16;

    public static ReadOnlySpan<byte> Prefix => Magic;

    public bool On => key is not null;

    /// <summary>The key for files from APP_KEY (its own, apart from the key ring's).</summary>
    public static byte[] Derive(string dataKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(dataKey), 32, info: Encoding.UTF8.GetBytes("arena files at rest v1"));

    public static bool IsSealed(ReadOnlySpan<byte> stored) => stored.Length >= Magic.Length + NonceSize + TagSize && stored[..Magic.Length].SequenceEqual(Magic);

    public byte[] Seal(byte[] plain)
    {
        if (key is null || IsSealed(plain))
        {
            return plain;
        }
        var sealedBytes = new byte[Magic.Length + NonceSize + TagSize + plain.Length];
        Magic.CopyTo(sealedBytes, 0);
        var nonce = sealedBytes.AsSpan(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(Magic.Length + NonceSize + TagSize), sealedBytes.AsSpan(Magic.Length + NonceSize, TagSize));
        return sealedBytes;
    }

    /// <summary>What sealed text starts with (a control character no extracted text holds), then the sealed bytes in base64.</summary>
    public const string TextMark = "\u0001ARENA-SEALED-1:";

    /// <summary>A file's text, sealed the same way; empty text stays empty (queries compare it with "").</summary>
    public string SealText(string plain) =>
        key is null || plain.Length == 0 || plain.StartsWith(TextMark, StringComparison.Ordinal) ? plain : TextMark + Convert.ToBase64String(Seal(Encoding.UTF8.GetBytes(plain)));

    public string OpenText(string stored) =>
        stored.StartsWith(TextMark, StringComparison.Ordinal) ? Encoding.UTF8.GetString(Open(Convert.FromBase64String(stored[TextMark.Length..]))) : stored;

    public byte[] Open(byte[] stored)
    {
        if (!IsSealed(stored))
        {
            return stored;
        }
        if (key is null)
        {
            throw new InvalidOperationException("A file is sealed with APP_KEY, and this app was given none (Auth:DataKey).");
        }
        var plain = new byte[stored.Length - Magic.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(stored.AsSpan(Magic.Length, NonceSize), stored.AsSpan(Magic.Length + NonceSize + TagSize), stored.AsSpan(Magic.Length + NonceSize, TagSize), plain);
        return plain;
    }
}

/// <summary>Carries the files' key in a context's options (Program.cs adds it from APP_KEY).</summary>
public sealed class FileKeyExtension(byte[]? key) : IDbContextOptionsExtension
{
    public byte[]? Key { get; } = key;

    /// <summary>Which key, without the key: the model (with its converters) is built once for each.</summary>
    public string Fingerprint { get; } = key is null ? "none" : Convert.ToHexString(SHA256.HashData(key)[..8]);

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(FileKeyExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"FileKey={extension.Fingerprint} ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["FileKey"] = extension.Fingerprint;
    }
}

/// <summary>One model for each files' key, as each has its own converters.</summary>
public sealed class FileKeyModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.GetService<IDbContextOptions>().FindExtension<FileKeyExtension>()?.Fingerprint ?? "none", designTime);
}
