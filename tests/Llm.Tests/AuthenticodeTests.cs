using System.Buffers.Binary;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Llm.Api.Downloads;

namespace Llm.Tests;

/// <summary>Code Arena's Windows builds Authenticode-signed, so Windows names their publisher: the signature, and the signing command.</summary>
public sealed class AuthenticodeTests
{
    // A real PE file: the app's own assembly (a .NET program is one, with the same headers and certificate table entry).
    private static byte[] Program() => File.ReadAllBytes(typeof(Authenticode).Assembly.Location);

    private static (uint At, uint Size) Table(byte[] pe)
    {
        var header = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        var optional = header + 24;
        var entry = optional + (BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(optional)) == 0x20B ? 112 : 96) + 4 * 8;
        return (BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry)), BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 4)));
    }

    [Fact]
    public void A_program_signed_names_its_signer_holds_only_for_its_own_bytes_and_signs_again_in_place()
    {
        var (ca, leaf) = DownloadTests.CodeSigning("authenticode");
        var program = Program();
        Assert.Null(Authenticode.Signer(program));

        var signed = Authenticode.Sign(program, leaf, [ca], "Code Arena");
        Assert.Equal(leaf.Thumbprint, Authenticode.Signer(signed)?.Thumbprint);
        // The image as it was, padded to 8 bytes, then the table: on an 8-byte boundary, to the end.
        var (at, size) = Table(signed);
        Assert.Equal(0u, at % 8);
        Assert.Equal(signed.Length, (int)(at + size));
        Assert.InRange(at, (uint)program.Length, (uint)program.Length + 7);

        // One byte of the program changed: the signature holds no more.
        var changed = (byte[])signed.Clone();
        changed[at / 2] ^= 0xFF;
        Assert.Null(Authenticode.Signer(changed));

        // Signed again (by another key): the old signature replaced, not added to.
        var (_, other) = DownloadTests.CodeSigning("other");
        var again = Authenticode.Sign(signed, other, [], "Code Arena");
        Assert.Equal(other.Thumbprint, Authenticode.Signer(again)?.Thumbprint);
        Assert.Equal(at, Table(again).At);
        Assert.Throws<InvalidDataException>(() => Authenticode.Sign("not a program"u8, leaf, [], "Code Arena"));
    }

    [Fact]
    public void The_signature_carries_what_Windows_reads_the_program_name_the_certificate_and_its_chain()
    {
        var (ca, leaf) = DownloadTests.CodeSigning("authenticode");
        var signed = Authenticode.Sign(Program(), leaf, [ca], "Code Arena");
        var (at, _) = Table(signed);
        var cms = new System.Security.Cryptography.Pkcs.SignedCms();
        var data = signed.AsSpan((int)at + 8);
        System.Formats.Asn1.AsnDecoder.ReadEncodedValue(data, System.Formats.Asn1.AsnEncodingRules.DER, out _, out _, out var used);
        cms.Decode(data[..used]);
        Assert.Equal("1.3.6.1.4.1.311.2.1.4", cms.ContentInfo.ContentType.Value);
        Assert.Equal(1, cms.Version);
        Assert.Equal(new[] { leaf.Thumbprint, ca.Thumbprint }.Order(StringComparer.Ordinal), cms.Certificates.Select(c => c.Thumbprint).Order(StringComparer.Ordinal));
        var signer = Assert.Single(cms.SignerInfos.Cast<System.Security.Cryptography.Pkcs.SignerInfo>());
        Assert.Equal("2.16.840.1.101.3.4.2.1", signer.DigestAlgorithm.Value);
        var opus = signer.SignedAttributes.Cast<System.Security.Cryptography.CryptographicAttributeObject>().Single(a => a.Oid.Value == "1.3.6.1.4.1.311.2.1.12");
        Assert.Contains("Code Arena", Encoding.BigEndianUnicode.GetString(opus.Values[0].RawData), StringComparison.Ordinal);
        // The PE checksum, as Windows computes it, with its own field taken as zero.
        var header = BinaryPrimitives.ReadInt32LittleEndian(signed.AsSpan(0x3C));
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(signed.AsSpan(header + 24 + 64));
        var copy = (byte[])signed.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(header + 24 + 64), 0);
        ulong sum = 0;
        for (var i = 0; i + 1 < copy.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(i));
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        Assert.Equal((uint)(sum + (ulong)copy.Length), stored);
    }

    private static async Task<(int Code, string Said, Dictionary<string, byte[]> Files)> SignAsync(string root, string pem)
    {
        using var output = new MemoryStream();
        using var said = new StringWriter();
        var code = await CodeArenaSigner.RunAsync(new MemoryStream(Encoding.ASCII.GetBytes(pem)), output, said, root);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (code == 0)
        {
            output.Position = 0;
            using var tar = new TarReader(output);
            while (await tar.GetNextEntryAsync() is { } entry)
            {
                using var data = new MemoryStream();
                await entry.DataStream!.CopyToAsync(data);
                files[entry.Name] = data.ToArray();
            }
        }
        return (code, said.ToString(), files);
    }

    [Fact]
    public async Task The_signing_command_signs_the_Windows_builds_the_image_lists_and_gives_the_list_of_what_it_serves()
    {
        var root = Directory.CreateTempSubdirectory("sign-").FullName;
        try
        {
            var (ca, leaf) = DownloadTests.CodeSigning("authenticode");
            Directory.CreateDirectory(Path.Combine(root, "win-x64"));
            Directory.CreateDirectory(Path.Combine(root, "linux-x64"));
            var exe = Program();
            await File.WriteAllBytesAsync(Path.Combine(root, "win-x64", "code-arena.exe"), exe);
            var linux = Encoding.ASCII.GetBytes("#!/bin/sh\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "linux-x64", "code-arena"), linux);
            var linuxLine = $"{Convert.ToHexStringLower(SHA256.HashData(linux))}  linux-x64/code-arena";
            var image = Encoding.ASCII.GetBytes($"{linuxLine}\n{Convert.ToHexStringLower(SHA256.HashData(exe))}  win-x64/code-arena.exe\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "SHA256SUMS"), image);
            // The key, then the certificate and its CA: as scripts/sign-code-arena.sh pipes signing/ in.
            var pem = leaf.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem() + "\n" + leaf.ExportCertificatePem() + "\n" + ca.ExportCertificatePem() + "\n";

            var (code, said, files) = await SignAsync(root, pem);
            Assert.True(code == 0, said);
            Assert.Contains("signed win-x64/code-arena.exe (Authenticode) as Code Arena (authenticode)", said, StringComparison.Ordinal);
            Assert.Equal(["SHA256SUMS", "SHA256SUMS.image", "win-x64/code-arena.exe"], files.Keys.Order(StringComparer.Ordinal));
            var signed = files["win-x64/code-arena.exe"];
            Assert.Equal(leaf.Thumbprint, Authenticode.Signer(signed)?.Thumbprint);
            Assert.Equal(image, files["SHA256SUMS.image"]);
            Assert.Equal($"{linuxLine}\n{Convert.ToHexStringLower(SHA256.HashData(signed))}  win-x64/code-arena.exe\n", Encoding.ASCII.GetString(files["SHA256SUMS"]));
            // Nothing of the key in what comes out (the program itself may say "PRIVATE KEY": the key's own bytes are what count).
            var key = leaf.GetRSAPrivateKey()!.ExportPkcs8PrivateKey();
            Assert.DoesNotContain(files.Values, f => f.AsSpan().IndexOf(key.AsSpan(0, 64)) >= 0);

            // A key of another certificate, or none: nothing signed.
            var (_, other) = DownloadTests.CodeSigning("other");
            (code, said, _) = await SignAsync(root, other.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem() + "\n" + leaf.ExportCertificatePem());
            Assert.Equal(2, code);
            Assert.Contains("no certificate there is the key's", said, StringComparison.Ordinal);

            // A build that is not the one the image lists is not signed.
            await File.WriteAllBytesAsync(Path.Combine(root, "win-x64", "code-arena.exe"), [.. exe, 0]);
            (code, said, _) = await SignAsync(root, pem);
            Assert.Equal(1, code);
            Assert.Contains("is not the build the image lists", said, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
