using System.Formats.Tar;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Downloads;

/// <summary>
/// dotnet Llm.Api.dll sign-code-arena [FOLDER]: Code Arena's Windows builds in this image (FOLDER, /downloads/code-arena:
/// code-arena.exe and code-arena-ide.exe beside it),
/// Authenticode-signed with the code-signing key and certificate read from standard input (PEM: the key, the certificate,
/// its chain), written to standard output as a tar of the signed copies, the builds' list with their SHA-256 (SHA256SUMS)
/// and the image's list it was made from (SHA256SUMS.image). scripts/sign-code-arena.sh runs it in a container of the app's
/// image with no network and nothing mounted: the key reaches it through a pipe, and leaves only as the signatures.
/// </summary>
public static partial class CodeArenaSigner
{
    /// <summary>What the UAC prompt names the program.</summary>
    public const string ProgramName = "Code Arena";

    /// <summary>A Windows build: code-arena.exe, and the IDE's launcher beside it (code-arena-ide.exe).</summary>
    [GeneratedRegex(@"^win-[a-z0-9]+/code-arena(-ide)?\.exe$")]
    private static partial Regex WindowsBuild();

    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter said, string root)
    {
        string pem;
        using (var reader = new StreamReader(input, Encoding.ASCII))
        {
            pem = await reader.ReadToEndAsync();
        }
        X509Certificate2 signer;
        var chain = new X509Certificate2Collection();
        try
        {
            chain.ImportFromPem(pem);
            signer = chain.Select(c => WithKey(c, pem)).FirstOrDefault(c => c is not null)
                ?? throw new CryptographicException("no certificate there is the key's");
        }
        catch (CryptographicException e)
        {
            await said.WriteLineAsync($"sign-code-arena: the key and certificate on standard input could not be read: {e.Message}");
            return 2;
        }
        var listFile = Path.Combine(root, "SHA256SUMS");
        if (!File.Exists(listFile))
        {
            await said.WriteLineAsync($"sign-code-arena: no Code Arena builds in {root}");
            return 3;
        }
        var image = await File.ReadAllBytesAsync(listFile);
        var served = new StringBuilder();
        await using (var tar = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var line in Encoding.ASCII.GetString(image).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var entry = parts.Length == 2 ? parts[1].TrimStart('*') : "";
                if (!WindowsBuild().IsMatch(entry))
                {
                    served.Append(line).Append('\n');
                    continue;
                }
                var build = await File.ReadAllBytesAsync(Path.Combine(root, entry));
                // Only what the image lists is signed: a build changed since it was published is no build of this release.
                if (!Convert.ToHexStringLower(SHA256.HashData(build)).Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                {
                    await said.WriteLineAsync($"sign-code-arena: {entry} is not the build the image lists: nothing signed");
                    return 1;
                }
                var signed = Authenticode.Sign(build, signer, chain, ProgramName);
                if (Authenticode.Signer(signed)?.Thumbprint != signer.Thumbprint)
                {
                    await said.WriteLineAsync($"sign-code-arena: {entry}'s signature did not check: nothing signed");
                    return 1;
                }
                await AddAsync(tar, entry, signed);
                served.Append(Convert.ToHexStringLower(SHA256.HashData(signed))).Append("  ").Append(entry).Append('\n');
                await said.WriteLineAsync($"sign-code-arena: signed {entry} (Authenticode) as {signer.GetNameInfo(X509NameType.SimpleName, false)}");
            }
            await AddAsync(tar, "SHA256SUMS", Encoding.ASCII.GetBytes(served.ToString()));
            await AddAsync(tar, "SHA256SUMS.image", image);
        }
        await output.FlushAsync();
        return 0;
    }

    /// <summary>The certificate with the PEM's private key, when the key is this certificate's; else null.</summary>
    private static X509Certificate2? WithKey(X509Certificate2 certificate, string pem)
    {
        try
        {
            return X509Certificate2.CreateFromPem(certificate.ExportCertificatePem(), pem);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task AddAsync(TarWriter tar, string name, byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = stream,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
        });
    }
}
