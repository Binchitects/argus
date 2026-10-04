using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed class DocumentPagesException(string message) : Exception(message);

/// <summary>
/// A document's pages as pictures, for its preview: a PDF as it is, Word, PowerPoint,
/// Excel and OpenDocument through LibreOffice to PDF first, all in the sandbox (no
/// network, the person's file never parsed in the app). Pages are drawn
/// <see cref="Batch"/> at a time, the first on first look and the next as the person
/// reaches them, each once, and kept with the file.
/// </summary>
public sealed partial class DocumentPages(SandboxClient sandbox, AppDbContext db)
{
    /// <summary>Pages drawn in one run: the most files the sandbox gives back from one.</summary>
    public const int Batch = 20;

    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".odt", ".rtf", ".pptx", ".ppt", ".odp", ".xlsx", ".xls", ".ods",
    };

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Drawing = new();

    /// <summary>
    /// The first line marks the job for the fake sandbox in tests; the second is the pages to
    /// draw. LibreOffice, in a job: its own profile, its pipe beside the job (the sandbox's
    /// /tmp is the runner's), and a second start when the first only made the profile (exit 81).
    /// </summary>
    private const string Script = """
        # pages
        first, last = FIRST, LAST
        import json, os, subprocess
        name = next(f for f in sorted(os.listdir('.')) if f.startswith('doc.'))
        pdf = name
        if not name.lower().endswith('.pdf'):
            tmp = os.path.abspath(os.environ.get('TMPDIR', '.tmp'))
            os.makedirs(tmp, exist_ok=True)
            env = dict(os.environ, OSL_SOCKET_PATH=tmp)
            cmd = ['/usr/lib/libreoffice/program/soffice.bin', '-env:UserInstallation=file://' + os.path.abspath('.lo'),
                   '--headless', '--norestore', '--convert-to', 'pdf', '--outdir', '.pdf', name]
            for _ in range(3):
                r = subprocess.run(cmd, capture_output=True, text=True, timeout=110, env=env)
                if r.returncode != 81:
                    break
            pdf = os.path.join('.pdf', os.path.splitext(name)[0] + '.pdf')
            if not os.path.exists(pdf):
                print(json.dumps({'error': 'The document could not be converted: ' + (r.stderr or r.stdout).strip()[-200:]}))
                raise SystemExit(0)
        info = subprocess.run(['pdfinfo', pdf], capture_output=True, text=True)
        total = next((int(l.split()[-1]) for l in info.stdout.splitlines() if l.startswith('Pages:')), 0)
        if info.returncode != 0 or total == 0:
            print(json.dumps({'error': 'The PDF cannot be read (damaged, or locked with a password).'}))
            raise SystemExit(0)
        r = subprocess.run(['pdftoppm', '-jpeg', '-jpegopt', 'quality=82', '-r', '110', '-f', str(first), '-l', str(min(last, total)), pdf, 'page'],
                           capture_output=True, text=True)
        print(json.dumps({'total': total, 'error': r.stderr.strip()[-200:] if r.returncode else None}))
        """;

    public static bool CanDraw(ChatAttachment a) => a.Data is not null && Kinds.Contains(Path.GetExtension(a.FileName));

    /// <summary>
    /// How many pages the document has, and how many are drawn (from the first): at least
    /// <paramref name="upTo"/>, or all it has, drawing the next <see cref="Batch"/> if need be.
    /// </summary>
    public async Task<(int Total, int Drawn)> PagesAsync(ChatAttachment a, int upTo, CancellationToken ct)
    {
        if (await KeptAsync(a.Id, ct) is { } kept && kept.Drawn >= Math.Min(upTo, kept.Total))
        {
            return kept;
        }
        var gate = Drawing.GetOrAdd(a.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var before = await KeptAsync(a.Id, ct);
            if (before is { } meanwhile && meanwhile.Drawn >= Math.Min(upTo, meanwhile.Total))
            {
                return meanwhile;
            }
            var first = (before?.Drawn ?? 0) + 1;
            var last = first + Batch - 1;
            if (sandbox.Unavailable() is { } why)
            {
                throw new DocumentPagesException(why.Replace("run code", "draw pages", StringComparison.Ordinal));
            }
            SandboxResult run;
            try
            {
                var script = Script.Replace("FIRST, LAST", $"{first}, {last}", StringComparison.Ordinal);
                run = await sandbox.RunAsync(script, [new SandboxFile("doc" + Path.GetExtension(a.FileName).ToLowerInvariant(), a.Data!)], 150, ct);
            }
            catch (SandboxException ex)
            {
                throw new DocumentPagesException(ex.Message);
            }
            var said = run.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
            using var doc = JsonDocument.Parse(said ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String && !root.TryGetProperty("total", out _))
            {
                throw new DocumentPagesException(e.GetString()!);
            }
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
            var pages = run.Files.Select(f => (Number: PageNumber(f.Name), f.Bytes)).Where(p => p.Number >= first && p.Number <= last).OrderBy(p => p.Number).ToList();
            if (pages.Count == 0)
            {
                throw new DocumentPagesException(run.Killed ?? run.Error ?? (e.ValueKind == JsonValueKind.String ? e.GetString()! : "No page could be drawn."));
            }
            var drawn = first - 1 + pages.Count;
            total = Math.Max(total, drawn);
            db.AttachmentPages.AddRange(pages.Select(p => new AttachmentPage { AttachmentId = a.Id, Number = p.Number, Total = total, Data = p.Bytes }));
            await db.SaveChangesAsync(ct);
            return (total, drawn);
        }
        finally
        {
            gate.Release();
            Drawing.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(a.Id, gate));
        }
    }

    private async Task<(int Total, int Drawn)?> KeptAsync(Guid id, CancellationToken ct)
    {
        var kept = await db.AttachmentPages.AsNoTracking().Where(p => p.AttachmentId == id).GroupBy(p => p.AttachmentId)
            .Select(g => new { Total = g.Max(p => p.Total), Drawn = g.Count() }).SingleOrDefaultAsync(ct);
        return kept is null ? null : (kept.Total, kept.Drawn);
    }

    private static int PageNumber(string name) => PageFile().Match(name) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"^page-0*(\d+)\.jpg$")]
    private static partial Regex PageFile();
}
