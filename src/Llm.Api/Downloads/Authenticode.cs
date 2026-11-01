using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Llm.Api.Downloads;

/// <summary>
/// Authenticode, the signature Windows reads in a program to name its publisher (what SmartScreen and the UAC prompt show in
/// place of "Unknown publisher"), made here because the signing runs on Linux, where there is no signtool. A PE file is signed
/// the way signtool and osslsigncode sign it with SHA-256: the image's hash (every byte but its checksum, the certificate
/// table's entry and the table) in Microsoft's SpcIndirectDataContent, in a PKCS #7 SignedData signed with the code-signing
/// key and carrying its certificate and chain, appended as a WIN_CERTIFICATE. No timestamp: there is no time-stamping
/// authority offline, so the signature holds while the certificate does (scripts/sign-code-arena.sh makes a new one, and
/// signs again, before its end).
/// </summary>
public static class Authenticode
{
    private const string SpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
    private const string SpcPeImageData = "1.3.6.1.4.1.311.2.1.15";
    private const string SpcSpOpusInfo = "1.3.6.1.4.1.311.2.1.12";
    private const string SpcStatementType = "1.3.6.1.4.1.311.2.1.11";
    private const string IndividualCodeSigning = "1.3.6.1.4.1.311.2.1.21";
    private const string Pkcs7SignedData = "1.2.840.113549.1.7.2";
    private const string ContentType = "1.2.840.113549.1.9.3";
    private const string MessageDigest = "1.2.840.113549.1.9.4";
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string RsaEncryption = "1.2.840.113549.1.1.1";
    private const string EcdsaSha256 = "1.2.840.10045.4.3.2";

    /// <summary>Where a PE file keeps what signing changes: its checksum, the certificate table's entry, and where the table starts (the end, when none).</summary>
    private readonly record struct Layout(int Checksum, int TableEntry, int TableAt);

    private static Layout Read(ReadOnlySpan<byte> pe)
    {
        if (pe.Length < 0x40 || pe[0] != 'M' || pe[1] != 'Z')
        {
            throw new InvalidDataException("not a Windows program: no MZ header");
        }
        var header = BinaryPrimitives.ReadInt32LittleEndian(pe[0x3C..]);
        if (header < 0x40 || header > pe.Length - 24 || !pe.Slice(header, 4).SequenceEqual("PE\0\0"u8))
        {
            throw new InvalidDataException("not a Windows program: no PE header");
        }
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe[(header + 20)..]);
        var optional = header + 24;
        if (optional + optionalSize > pe.Length || optionalSize < 2)
        {
            throw new InvalidDataException("the PE header is cut short");
        }
        var (count, directories) = BinaryPrimitives.ReadUInt16LittleEndian(pe[optional..]) switch
        {
            0x10B => (optional + 92, optional + 96),
            0x20B => (optional + 108, optional + 112),
            _ => throw new InvalidDataException("the PE header is neither PE32 nor PE32+"),
        };
        if (directories + 5 * 8 > optional + optionalSize || BinaryPrimitives.ReadUInt32LittleEndian(pe[count..]) < 5)
        {
            throw new InvalidDataException("the PE header has no certificate table entry");
        }
        var entry = directories + 4 * 8;
        var at = BinaryPrimitives.ReadUInt32LittleEndian(pe[entry..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(pe[(entry + 4)..]);
        if (at == 0 && size == 0)
        {
            return new Layout(optional + 64, entry, pe.Length);
        }
        // A signature already there is at the file's end: signing again puts the new one in its place.
        if (at < entry + 8 || (long)at + size != pe.Length)
        {
            throw new InvalidDataException("the certificate table is not at the program's end");
        }
        return new Layout(optional + 64, entry, (int)at);
    }

    /// <summary>The image's hash as Authenticode takes it: every byte before the table but the checksum and the table's entry.</summary>
    private static byte[] Hash(ReadOnlySpan<byte> pe, Layout layout)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(pe[..layout.Checksum]);
        hash.AppendData(pe[(layout.Checksum + 4)..layout.TableEntry]);
        hash.AppendData(pe[(layout.TableEntry + 8)..layout.TableAt]);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// The program signed by <paramref name="signer"/> (its certificate with the private key), carrying <paramref name="chain"/>
    /// (its CA and any between) for Windows to build the path to a root it trusts; <paramref name="name"/> is the program's
    /// name the UAC prompt shows. A program signed before is signed again, the old signature gone.
    /// </summary>
    public static byte[] Sign(ReadOnlySpan<byte> program, X509Certificate2 signer, IEnumerable<X509Certificate2> chain, string name)
    {
        var layout = Read(program);
        // The table starts on an 8-byte boundary: the zeros that put it there are the image's, and hashed.
        var length = (layout.TableAt + 7) / 8 * 8;
        var pe = new byte[length];
        program[..layout.TableAt].CopyTo(pe);
        layout = layout with { TableAt = length };
        var content = IndirectData(Hash(pe, layout));
        var pkcs7 = SignedData(content, signer, chain, name);
        var table = (pkcs7.Length + 8 + 7) / 8 * 8;
        var signed = new byte[length + table];
        pe.CopyTo(signed);
        // WIN_CERTIFICATE: its length (with the padding), revision 2.0, PKCS #7 SignedData.
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(length), (uint)table);
        BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(length + 4), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(length + 6), 0x0002);
        pkcs7.CopyTo(signed.AsSpan(length + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(layout.TableEntry), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(layout.TableEntry + 4), (uint)table);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(layout.Checksum), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(layout.Checksum), Checksum(signed));
        return signed;
    }

    /// <summary>
    /// Who signed this program, when its signature holds for exactly these bytes (the key signed the hash it carries, and that
    /// is the image's hash now); null when it is not signed, or was changed after. Whether Windows trusts the signer's CA is
    /// Windows' to say.
    /// </summary>
    public static X509Certificate2? Signer(ReadOnlySpan<byte> program)
    {
        try
        {
            var layout = Read(program);
            if (layout.TableAt == program.Length || program.Length - layout.TableAt < 8
                || BinaryPrimitives.ReadUInt16LittleEndian(program[(layout.TableAt + 6)..]) != 0x0002)
            {
                return null;
            }
            var data = program[(layout.TableAt + 8)..];
            AsnDecoder.ReadEncodedValue(data, AsnEncodingRules.BER, out _, out _, out var used);
            var cms = new SignedCms();
            cms.Decode(data[..used]);
            if (cms.ContentInfo.ContentType.Value != SpcIndirectData || cms.SignerInfos.Count != 1)
            {
                return null;
            }
            cms.CheckSignature(verifySignatureOnly: true);
            // The content as written (PKCS #7 keeps it as is; CMS in an OCTET STRING): SpcIndirectDataContent.
            var content = cms.ContentInfo.Content;
            if (content.Length > 0 && content[0] == 0x04)
            {
                content = AsnDecoder.ReadOctetString(content, AsnEncodingRules.BER, out _);
            }
            var indirect = new AsnReader(content, AsnEncodingRules.BER).ReadSequence();
            indirect.ReadSequence();
            var digest = indirect.ReadSequence();
            digest.ReadSequence();
            return digest.ReadOctetString().AsSpan().SequenceEqual(Hash(program, layout)) ? cms.SignerInfos[0].Certificate : null;
        }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or AsnContentException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The PE checksum: the file's 16-bit words summed with their carries folded in, plus its length.</summary>
    private static uint Checksum(ReadOnlySpan<byte> pe)
    {
        ulong sum = 0;
        for (var i = 0; i + 1 < pe.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(pe[i..]);
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        if (pe.Length % 2 == 1)
        {
            sum += pe[^1];
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (uint)(((sum & 0xFFFF) + (sum >> 16)) + (ulong)pe.Length);
    }

    /// <summary>SpcIndirectDataContent: "a PE image" (SpcPeImageData, with signtool's obsolete file link) and its SHA-256.</summary>
    private static byte[] IndirectData(byte[] hash)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(SpcPeImageData);
                using (w.PushSequence())
                {
                    w.WriteBitString([]);
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    {
                        w.WriteCharacterString(UniversalTagNumber.BMPString, "<<<Obsolete>>>", new Asn1Tag(TagClass.ContextSpecific, 0));
                    }
                }
            }
            using (w.PushSequence())
            {
                Algorithm(w, Sha256, parameters: true);
                w.WriteOctetString(hash);
            }
        }
        return w.Encode();
    }

    private static void Algorithm(AsnWriter w, string oid, bool parameters)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            if (parameters)
            {
                w.WriteNull();
            }
        }
    }

    private static void Attribute(AsnWriter w, string oid, Action<AsnWriter> value)
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            using (w.PushSetOf())
            {
                value(w);
            }
        }
    }

    /// <summary>The PKCS #7 SignedData of the content: version 1, SHA-256, the certificates, one signer with the attributes Windows reads.</summary>
    private static byte[] SignedData(byte[] content, X509Certificate2 signer, IEnumerable<X509Certificate2> chain, string name)
    {
        // PKCS #7 hashes the content's value: the SEQUENCE's contents, not its tag and length.
        AsnDecoder.ReadEncodedValue(content, AsnEncodingRules.DER, out var offset, out var size, out _);
        var digest = SHA256.HashData(content.AsSpan(offset, size));
        var attributes = new AsnWriter(AsnEncodingRules.DER);
        using (attributes.PushSetOf())
        {
            Attribute(attributes, ContentType, a => a.WriteObjectIdentifier(SpcIndirectData));
            Attribute(attributes, SpcSpOpusInfo, a =>
            {
                using (a.PushSequence())
                using (a.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    a.WriteCharacterString(UniversalTagNumber.BMPString, name, new Asn1Tag(TagClass.ContextSpecific, 0));
                }
            });
            Attribute(attributes, SpcStatementType, a =>
            {
                using (a.PushSequence())
                {
                    a.WriteObjectIdentifier(IndividualCodeSigning);
                }
            });
            Attribute(attributes, MessageDigest, a => a.WriteOctetString(digest));
        }
        // The key signs the attributes as a SET; the SignerInfo holds them as [0].
        var signedAttributes = attributes.Encode();
        var (algorithm, signature) = SignWithKey(signer, signedAttributes);
        signedAttributes[0] = 0xA0;

        var certificates = new List<X509Certificate2> { signer };
        certificates.AddRange(chain.Where(c => c.Thumbprint != signer.Thumbprint));
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(Pkcs7SignedData);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())
            {
                w.WriteInteger(1);
                using (w.PushSetOf())
                {
                    Algorithm(w, Sha256, parameters: true);
                }
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier(SpcIndirectData);
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    {
                        w.WriteEncodedValue(content);
                    }
                }
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    foreach (var certificate in certificates)
                    {
                        w.WriteEncodedValue(certificate.RawData);
                    }
                }
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteInteger(1);
                    using (w.PushSequence())
                    {
                        w.WriteEncodedValue(signer.IssuerName.RawData);
                        w.WriteInteger(signer.SerialNumberBytes.Span);
                    }
                    Algorithm(w, Sha256, parameters: true);
                    w.WriteEncodedValue(signedAttributes);
                    Algorithm(w, algorithm, parameters: algorithm == RsaEncryption);
                    w.WriteOctetString(signature);
                }
            }
        }
        return w.Encode();
    }

    private static (string Algorithm, byte[] Signature) SignWithKey(X509Certificate2 signer, byte[] data)
    {
        using (var rsa = signer.GetRSAPrivateKey())
        {
            if (rsa is not null)
            {
                return (RsaEncryption, rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
        }
        using var ecdsa = signer.GetECDsaPrivateKey()
            ?? throw new CryptographicException("the code-signing certificate has no RSA or ECDSA private key with it");
        return (EcdsaSha256, ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }
}
