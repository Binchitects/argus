using System.Runtime.CompilerServices;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Knowledge;

/// <summary>
/// Files uploaded into a source (Admin → Knowledge → Uploaded files), kept in the database: each read as
/// <see cref="DocumentReader"/> reads files (OCR for pictures and scans, older Office files converted), and an archive's
/// files each as a document of their own ("handbook.zip/hr/leave.docx"). A file is read again only when it changed (its
/// SHA-256). Who may read them is who the admin chose.
/// </summary>
public sealed class UploadConnector(AppDbContext db, DocumentReader reader) : IKnowledgeConnector
{
    public string Kind => "upload";

    public string? Check(KnowledgeSource source) => null;

    public async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        List<string> readers = [Readers.Chosen(source.Id)];
        var files = await db.KnowledgeFiles.AsNoTracking().Where(f => f.SourceId == source.Id).OrderBy(f => f.Path)
            .Select(f => new { f.Id, f.Path, f.Sha256 }).ToListAsync(ct);
        foreach (var f in files)
        {
            if (!DocumentReader.IsArchive(f.Path))
            {
                yield return new FoundDocument(f.Path, f.Path, null, f.Sha256, readers, async token => await ReadAsync(f.Path, await BytesAsync(f.Id, token), pass, token));
                continue;
            }
            // An archive: its files, each read from it when it changed.
            List<string> members;
            try
            {
                members = [.. DocumentReader.Members(await BytesAsync(f.Id, ct), KnowledgeFile.MaxBytes).Select(m => m.Path)];
            }
            catch (InvalidDataException)
            {
                pass.Problems.Add($"{f.Path} is not an archive that can be opened.");
                continue;
            }
            foreach (var member in members)
            {
                var key = $"{f.Path}/{member}";
                yield return new FoundDocument(key, key, null, $"{f.Sha256}:{member}", readers, async token =>
                {
                    var bytes = DocumentReader.Members(await BytesAsync(f.Id, token), KnowledgeFile.MaxBytes).First(m => m.Path == member).Bytes;
                    return await ReadAsync(key, bytes, pass, token);
                });
            }
        }
    }

    private async Task<byte[]> BytesAsync(Guid id, CancellationToken ct) =>
        await db.KnowledgeFiles.AsNoTracking().Where(f => f.Id == id).Select(f => f.Bytes).SingleAsync(ct);

    private async Task<string?> ReadAsync(string name, byte[] bytes, SyncPass pass, CancellationToken ct)
    {
        var read = await reader.ReadAsync(name, bytes, ct);
        if (read.Problem is { } problem)
        {
            lock (pass.Problems)
            {
                pass.Problems.Add(problem);
            }
        }
        // Not read at all (the sandbox off): not kept as empty, so a later sync reads it.
        return read.Problem is not null && read.Text.Length == 0 ? null : read.Text;
    }
}
