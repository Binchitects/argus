using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;

namespace Llm.Api.Vector;

/// <summary>What a picture is traced as: how many colours it keeps, how small a speck it drops, how closely it follows edges.</summary>
public sealed record TraceStyle(string Name, string About, int Speckle, int ColorPrecision, int LayerDifference, int Corner, double Length, int PathPrecision);

/// <summary>
/// The sandbox's part in vector work: a picture traced into vector shapes (vtracer: colour, stacked, smooth curves), and an
/// animated drawing's frames made a video (MP4, a WebM that keeps its see-through background, or a looping GIF) by ffmpeg.
/// </summary>
public sealed class VectorSandbox(SandboxClient sandbox)
{
    /// <summary>The styles a picture is traced in: from few flat colours (an icon, a logo) to many (an illustration with detail).</summary>
    public static readonly IReadOnlyList<TraceStyle> Styles =
    [
        new("illustration", "a flat illustration: clean shapes, its colours kept", 8, 6, 24, 60, 4.0, 2),
        new("detailed", "more colours and smaller details", 4, 7, 12, 60, 3.5, 2),
        new("icon", "few bold colours, small specks dropped", 16, 4, 40, 70, 6.0, 1),
        new("logo", "sharp corners, flat colours", 12, 5, 32, 45, 4.0, 1),
    ];

    public static TraceStyle StyleOf(string? name) => Styles.FirstOrDefault(s => s.Name == name) ?? Styles[0];

    private const string TraceScript = """
        import json, vtracer
        job = json.load(open('job.json'))
        svg = vtracer.convert_raw_image_to_svg(open('picture.' + job['format'], 'rb').read(), img_format=job['format'], colormode='color',
            hierarchical='stacked', mode='spline', filter_speckle=job['speckle'], color_precision=job['color_precision'],
            layer_difference=job['layer_difference'], corner_threshold=job['corner'], length_threshold=job['length'],
            max_iterations=10, splice_threshold=45, path_precision=job['path_precision'])
        open('traced.svg', 'w', encoding='utf-8').write(svg)
        print(json.dumps({'paths': svg.count('<path')}))
        """;

    /// <summary>A picture (PNG or JPEG) traced into an SVG of coloured shapes, in its own pixels' units.</summary>
    public async Task<string> TraceAsync(byte[] picture, string type, TraceStyle style, CancellationToken ct)
    {
        if (sandbox.Unavailable() is { } why)
        {
            throw new SvgException($"Pictures are traced in the sandbox, which does not run here ({why}).");
        }
        var format = type.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || type.Contains("jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
        var job = new JsonObject
        {
            ["format"] = format, ["speckle"] = style.Speckle, ["color_precision"] = style.ColorPrecision, ["layer_difference"] = style.LayerDifference,
            ["corner"] = style.Corner, ["length"] = style.Length, ["path_precision"] = style.PathPrecision,
        };
        var run = await sandbox.RunAsync(TraceScript, [new SandboxFile("picture." + format, picture), new SandboxFile("job.json", Encoding.UTF8.GetBytes(job.ToJsonString()))], 120, ct);
        var svg = run.Files.FirstOrDefault(f => f.Name == "traced.svg")?.Bytes;
        return svg is not null ? Encoding.UTF8.GetString(svg) : throw new SvgException($"The picture could not be traced: {Tail(run)}");
    }

    private static string Tail(SandboxResult run) =>
        run.Killed ?? run.Error ?? (run.Stderr.Length > 0 ? run.Stderr[^Math.Min(300, run.Stderr.Length)..].Trim() : "nothing came back");

    /// <summary>How the frames are made a video: its filters and codec, by format (the frames are PNGs, see-through where nothing is drawn).</summary>
    private const string EncodeScript = """
        import json, subprocess
        job = json.load(open('job.json'))
        fps, fmt, bg = job['fps'], job['format'], job['background']
        src = ['-framerate', str(fps), '-i', 'frame-%05d.png']
        # Few threads: the sandbox allows a run 64 processes and 3 GB of address space.
        base = ['ffmpeg', '-v', 'error', '-y', '-filter_threads', '1', '-filter_complex_threads', '1', '-threads', '1']
        even = 'pad=ceil(iw/2)*2:ceil(ih/2)*2:color=0x00000000'
        if fmt == 'mp4':
            flat = f'color=c={bg}:s=16x16:r={fps}[bg];[bg][0:v]scale2ref[bg2][fg];[bg2][fg]overlay=shortest=1,{even},format=yuv420p'
            cmd = base + src + ['-filter_complex', flat, '-c:v', 'libx264', '-threads', '2', '-x264-params', 'threads=2:lookahead-threads=1',
                                '-preset', 'medium', '-crf', '18', '-movflags', '+faststart', 'drawing.mp4']
        elif fmt == 'webm':
            cmd = base + src + ['-vf', f'{even},format=yuva420p', '-c:v', 'libvpx-vp9', '-threads', '2', '-row-mt', '0', '-pix_fmt', 'yuva420p',
                                '-b:v', '0', '-crf', '30', '-auto-alt-ref', '0', 'drawing.webm']
        else:
            pal = '[0:v]split[a][b];[a]palettegen=reserve_transparent=1:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a:alpha_threshold=128'
            cmd = base + src + ['-filter_complex', pal, '-loop', '0', 'drawing.gif']
        r = subprocess.run(cmd, capture_output=True, text=True)
        print(json.dumps({'error': r.stderr.strip()[-600:] if r.returncode else None}))
        """;

    /// <summary>Frames (PNGs) made a video: "mp4" (on <paramref name="background"/>), "webm" (see-through kept) or "gif" (looping).</summary>
    public async Task<(byte[] Bytes, string Type, string Extension)> EncodeAsync(IReadOnlyList<byte[]> frames, int fps, string format, string background, CancellationToken ct)
    {
        if (sandbox.Unavailable() is { } why)
        {
            throw new SvgException($"Videos are made in the sandbox, which does not run here ({why}).");
        }
        var files = frames.Select((f, i) => new SandboxFile(string.Create(CultureInfo.InvariantCulture, $"frame-{i:00000}.png"), f)).ToList();
        files.Add(new SandboxFile("job.json", Encoding.UTF8.GetBytes(new JsonObject { ["fps"] = fps, ["format"] = format, ["background"] = background }.ToJsonString())));
        var run = await sandbox.RunAsync(EncodeScript, files, 280, ct);
        var (name, type) = format switch { "mp4" => ("drawing.mp4", "video/mp4"), "webm" => ("drawing.webm", "video/webm"), _ => ("drawing.gif", "image/gif") };
        var said = run.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        var error = said is null ? null : JsonNode.Parse(said)?["error"]?.GetValue<string>();
        var bytes = run.Files.FirstOrDefault(f => f.Name == name)?.Bytes;
        return bytes is not null && error is null
            ? (bytes, type, Path.GetExtension(name))
            : throw new SvgException($"The {format.ToUpperInvariant()} could not be made: {error ?? Tail(run)}");
    }
}
