using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>
/// A folder the app can read: one the admin mounts under the folder root (/knowledge, in
/// docker-compose.override.yml), and nothing outside it. Its files, subfolders too, as the chat reads
/// attachments (text, Markdown, HTML, PDF, Office and OpenDocument files); hidden files and links are
/// skipped. Who may read them is who the admin chose.
/// </summary>
public sealed class FolderConnector(IOptionsMonitor<KnowledgeOptions> options) : IKnowledgeConnector
{
    public const int MaxFiles = 20_000;
    private const long MaxBytes = 50 * 1024 * 1024;
    private const int MaxChars = 2_000_000;

    public string Kind => "folder";

    public string? Check(KnowledgeSource source)
    {
        var root = Path.GetFullPath(options.CurrentValue.FolderRoot);
        var path = Path.GetFullPath(source.Location.Trim());
        return path == root || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal)
            ? null
            : $"A folder must be under {root}: mount it there (docker-compose.override.yml), read-only.";
    }

    public async IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, [EnumeratorCancellation] CancellationToken ct)
    {
        var folder = Path.GetFullPath(source.Location.Trim());
        if (!Directory.Exists(folder))
        {
            throw new KnowledgeException($"The folder {folder} does not exist, or the app cannot read it. Mount it in docker-compose.override.yml.");
        }
        var readers = Readers.For(source.Audience, source.Groups);
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var count = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", walk))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            // Hidden folders (.git) too, not only hidden files.
            if (relative.Split('/').Any(part => part.StartsWith('.')))
            {
                continue;
            }
            if (++count > MaxFiles)
            {
                pass.Incomplete = true;
                pass.Problems.Add($"Only the first {MaxFiles:N0} files are read.");
                break;
            }
            var info = new FileInfo(file);
            if (info.Length is 0 or > MaxBytes)
            {
                skipped++;
                continue;
            }
            var version = string.Create(CultureInfo.InvariantCulture, $"{info.LastWriteTimeUtc.Ticks}:{info.Length}");
            yield return new FoundDocument(relative, relative, null, version, readers, async token =>
            {
                var bytes = await File.ReadAllBytesAsync(file, token);
                return Read(relative, bytes);
            });
        }
        await Task.CompletedTask;
        if (skipped > 0)
        {
            pass.Problems.Add($"{skipped} files were skipped: empty, or larger than 50 MB.");
        }
    }

    /// <summary>A file's text as the chat reads it; an HTML page as its readable text. Empty when it is not a file the chat reads.</summary>
    public static string Read(string name, byte[] bytes)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".html" or ".htm")
        {
            var (title, text) = Html.Read(Encoding.UTF8.GetString(bytes));
            return title.Length > 0 ? $"# {title}\n\n{text}" : text;
        }
        try
        {
            return Attachments.Extract(name, "", bytes, MaxChars).Text;
        }
        catch (AttachmentException)
        {
            return "";
        }
    }
}
