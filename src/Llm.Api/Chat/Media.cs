using System.Text.Json;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed class MediaException(string message) : Exception(message);

/// <summary>
/// Sound and video attachments. On upload the sandbox's ffmpeg makes what the models take: a sound as
/// an MP3 (mono, 16 kHz), a video as up to eight frames and its sound track. A model that hears gets
/// the sound itself; one that does not gets a transcript (speech to text at the gateway), made once.
/// </summary>
public sealed partial class Media(SandboxClient sandbox, AppDbContext db, GatewayChat gateway, ChatModels models, VoiceCatalog voices, Gateway.Credit credit)
{
    public const int MaxFrames = 8;

    /// <summary>How the page names a recording from its microphone (sound.ts): the person's own speech.</summary>
    public const string VoiceMessage = "Voice message";

    private const string Script = """
        import json, os, subprocess
        src = next(f for f in os.listdir('.') if f.startswith('media.'))
        kind = open('kind').read().strip()
        probe = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration:stream=codec_type', '-of', 'json', src],
                               capture_output=True, text=True)
        info = json.loads(probe.stdout or '{}')
        seconds = float(info.get('format', {}).get('duration') or 0)
        streams = {s.get('codec_type') for s in info.get('streams', [])}
        out = {'seconds': seconds, 'sound': False, 'frames': 0}
        if not streams:
            out['error'] = 'This is not a sound or video file ffmpeg can read.'
        if 'audio' in streams:
            r = subprocess.run(['ffmpeg', '-v', 'error', '-y', '-threads', '2', '-i', src, '-vn', '-ac', '1', '-ar', '16000', '-b:a', '64k', 'sound.mp3'],
                               capture_output=True, text=True)
            out['sound'] = r.returncode == 0
            if r.returncode: out['error'] = r.stderr.strip()[-300:]
        if kind == 'video' and 'video' in streams:
            n = min(8, max(1, int(seconds // 3) + 1)) if seconds > 0 else 1
            for i in range(n):
                at = seconds * (i + 0.5) / n if seconds > 0 else 0
                # Few threads: the sandbox allows each run 64 processes, and a decoder takes one a core.
                r = subprocess.run(['ffmpeg', '-v', 'error', '-y', '-threads', '2', '-ss', f'{at:.2f}', '-i', src, '-frames:v', '1', '-threads', '2',
                                    '-filter_threads', '1', '-vf', 'scale=512:512:force_original_aspect_ratio=decrease', '-q:v', '4', f'frame-{i + 1:02d}.jpg'],
                                   capture_output=True, text=True)
                if r.returncode: out['error'] = r.stderr.strip()[-300:]
            out['frames'] = n
        print(json.dumps(out))
        """;

    /// <summary>"audio" or "video" from the file's first bytes (the browser's type and the name only tell a WebM or MP4 sound from a video), else null.</summary>
    public static (string Kind, string ContentType)? Detect(string fileName, string? browserType, byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var sound = (browserType ?? "").StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || ext is ".m4a" or ".aac" or ".weba" or ".opus" or ".oga";
        if (b.Length < 12)
        {
            return null;
        }
        if (b[..4].SequenceEqual("RIFF"u8))
        {
            return b[8..12].SequenceEqual("WAVE"u8) ? ("audio", "audio/wav") : b[8..11].SequenceEqual("AVI"u8) ? ("video", "video/x-msvideo") : null;
        }
        if (b.StartsWith("ID3"u8) || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0 && (b[1] & 0x06) != 0))
        {
            return ("audio", "audio/mpeg");
        }
        if (b[0] == 0xFF && (b[1] & 0xF6) == 0xF0)
        {
            return ("audio", "audio/aac");
        }
        if (b.StartsWith("OggS"u8))
        {
            return ("audio", "audio/ogg");
        }
        if (b.StartsWith("fLaC"u8))
        {
            return ("audio", "audio/flac");
        }
        if (b[4..8].SequenceEqual("ftyp"u8))
        {
            return sound || b[8..12].SequenceEqual("M4A "u8) ? ("audio", "audio/mp4")
                : b[8..12].SequenceEqual("qt  "u8) ? ("video", "video/quicktime") : ("video", "video/mp4");
        }
        if (b.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
        {
            return sound ? ("audio", "audio/webm") : ext == ".mkv" ? ("video", "video/x-matroska") : ("video", "video/webm");
        }
        return null;
    }

    /// <summary>
    /// Makes what the models take, once, on upload: a sound becomes its MP3 (kept as the file),
    /// a video keeps its own bytes and gains frames (as pages) and its sound track.
    /// </summary>
    public async Task PrepareAsync(ChatAttachment a, CancellationToken ct)
    {
        if (sandbox.Unavailable() is { } why)
        {
            throw new MediaException(why.Replace("run code", "read sound and video", StringComparison.Ordinal));
        }
        SandboxResult run;
        try
        {
            run = await sandbox.RunAsync(Script, [new SandboxFile("media" + Extension(a.ContentType), a.Data!), new SandboxFile("kind", System.Text.Encoding.UTF8.GetBytes(a.Kind))], 240, ct);
        }
        catch (SandboxException ex)
        {
            throw new MediaException(ex.Message);
        }
        var said = run.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (said is null)
        {
            throw new MediaException(run.Killed ?? run.Error ?? $"{a.FileName} could not be read.");
        }
        using var doc = JsonDocument.Parse(said);
        var root = doc.RootElement;
        var seconds = root.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var sec) ? sec : 0;
        var mp3 = run.Files.FirstOrDefault(f => f.Name == "sound.mp3")?.Bytes;
        var frames = run.Files.Select(f => (Number: FrameNumber(f.Name), f.Bytes)).Where(f => f.Number > 0).OrderBy(f => f.Number).ToList();
        if (a.Kind == "audio")
        {
            if (mp3 is null)
            {
                throw new MediaException(root.TryGetProperty("error", out var e) ? $"{a.FileName}: {e.GetString()}" : $"{a.FileName} has no sound ffmpeg can read.");
            }
            (a.Data, a.ContentType, a.Size) = (mp3, "audio/mpeg", mp3.Length);
            a.FileName = Path.ChangeExtension(a.FileName, ".mp3");
        }
        else
        {
            if (frames.Count == 0)
            {
                throw new MediaException(root.TryGetProperty("error", out var e) ? $"{a.FileName}: {e.GetString()}" : $"{a.FileName} has no picture ffmpeg can read.");
            }
            a.Sound = mp3;
            db.AttachmentPages.AddRange(frames.Select(f => new AttachmentPage { AttachmentId = a.Id, Number = f.Number, Total = frames.Count, Data = f.Bytes }));
        }
        a.Seconds = seconds;
    }

    /// <summary>
    /// What was said in it (empty for silence or no sound), made once at the gateway's speech to text and kept. A voice
    /// message is heard in the language its person speaks, when they chose one; any other sound in the language Whisper hears.
    /// </summary>
    public async Task<string> TranscriptAsync(ChatAttachment a, string email, CancellationToken ct)
    {
        if (a.Text.Length > 0)
        {
            return a.Text;
        }
        var sound = a.Kind == "audio" ? a.Data : a.Sound;
        if (sound is null)
        {
            return "";
        }
        var model = await models.OfModeAsync("audio_transcription", Models.MediaModels.SpeechToText, ct)
            ?? throw new MediaException("The gateway has no speech to text model (the audio module).");
        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == a.UserId, ct);
        // Sound turned into text counts to its owner's speech credit.
        if (owner is not null && await credit.RefusalAsync(owner, Llm.Core.Access.CreditKind.Speech, ct) is { } spent)
        {
            throw new MediaException(spent);
        }
        var person = a.FileName.StartsWith(VoiceMessage, StringComparison.Ordinal) ? owner : null;
        var text = (await gateway.TranscribeAsync(model.Name, sound, "sound.mp3", email, ct, language: person is null ? null : await voices.LanguageOfAsync(person, ct))).Trim();
        await db.ChatAttachments.Where(x => x.Id == a.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Text, text.Length == 0 ? " " : text), ct);
        a.Text = text.Length == 0 ? " " : text;
        return text;
    }

    /// <summary>A video's frames, in order, with the second each was taken at.</summary>
    public async Task<IReadOnlyList<(double At, byte[] Jpeg)>> FramesAsync(ChatAttachment a, CancellationToken ct)
    {
        var frames = await db.AttachmentPages.AsNoTracking().Where(p => p.AttachmentId == a.Id).OrderBy(p => p.Number).ToListAsync(ct);
        var seconds = a.Seconds ?? 0;
        return [.. frames.Select((f, i) => (Math.Round(seconds * (i + 0.5) / frames.Count, 1), f.Data))];
    }

    private static string Extension(string contentType) => contentType switch
    {
        "audio/wav" => ".wav", "audio/mpeg" => ".mp3", "audio/aac" => ".aac", "audio/ogg" => ".ogg", "audio/flac" => ".flac", "audio/mp4" => ".m4a",
        "audio/webm" => ".webm", "video/x-msvideo" => ".avi", "video/quicktime" => ".mov", "video/x-matroska" => ".mkv", "video/webm" => ".webm", _ => ".mp4",
    };

    private static int FrameNumber(string name) => FrameFile().Match(name) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"^frame-0*(\d+)\.jpg$")]
    private static partial Regex FrameFile();
}
