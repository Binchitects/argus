using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;

namespace Llm.Api.Vector;

/// <summary>What a picture is traced as: how many colours it keeps, how small a speck it drops, how closely it follows edges.</summary>
public sealed record TraceStyle(string Name, string About, int Speckle, int ColorPrecision, int LayerDifference, int Corner, double Length, int PathPrecision);

/// <summary>
/// The sandbox's part in vector work: a picture traced into vector shapes (vtracer: colour, cut out, smooth curves) made parts, and an
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

    /// <summary>
    /// The tracing (vtracer, cut out: shapes side by side, none under another, so a part can move apart), and the parts made
    /// of its shapes: the background one rect of its colour; each thing named where the segmenter saw it (its maps), by the
    /// shapes seen there, a shape not sure taking its neighbour's; a thing in many islands (stars) a group of one group each;
    /// what is left a part for each island. Each shape gets a hairline of its own colour, so no seam shows.
    /// </summary>
    private const string TraceScript = """
        import io, json, re
        import numpy as np
        import resvg_py, vtracer
        from PIL import Image
        from scipy import ndimage

        job = json.load(open('job.json'))
        svg = vtracer.convert_raw_image_to_svg(open('picture.' + job['format'], 'rb').read(), img_format=job['format'], colormode='color',
            hierarchical='cutout', mode='spline', filter_speckle=job['speckle'], color_precision=job['color_precision'],
            layer_difference=job['layer_difference'], corner_threshold=job['corner'], length_threshold=job['length'],
            max_iterations=10, splice_threshold=45, path_precision=job['path_precision'])
        W, H = (int(v) for v in re.search(r'width="(\d+)" height="(\d+)"', svg).groups())
        paths = re.findall(r'<path[^>]*/>', svg)
        fills = [re.search(r'fill="(#[0-9A-Fa-f]{6})"', p).group(1) for p in paths]
        rgb = np.array([[int(f[i:i + 2], 16) for i in (1, 3, 5)] for f in fills], dtype=float)

        # Which shape is seen at each point: each painted its own number as a colour, edges not smoothed.
        S = 512
        scale = S / max(W, H)
        sw, sh = max(1, round(W * scale)), max(1, round(H * scale))
        coded = ''.join(re.sub(r'fill="#[0-9A-Fa-f]{6}"', f'fill="#{i + 1:06x}"', p) for i, p in enumerate(paths))
        idsvg = f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" shape-rendering="crispEdges">{coded}</svg>'
        px = np.asarray(Image.open(io.BytesIO(bytes(resvg_py.svg_to_bytes(svg_string=idsvg, width=sw, height=sh)))).convert('RGBA'), dtype=np.int64)
        seen = np.where(px[..., 3] > 0, (px[..., 0] << 16) | (px[..., 1] << 8) | px[..., 2], 0) - 1  # -1: nothing
        n = len(paths)
        area = np.bincount(seen[seen >= 0], minlength=n)

        # The background: the colour most seen at the edges, and the shapes of about that colour that reach an edge.
        edge = np.concatenate([seen[0], seen[-1], seen[:, 0], seen[:, -1]])
        edge = edge[edge >= 0]
        bg = rgb[np.bincount(edge, minlength=n).argmax()] if len(edge) else np.array([255.0, 255.0, 255.0])
        touches = np.zeros(n, bool)
        touches[np.unique(edge)] = True
        back = touches & (np.abs(rgb - bg).sum(1) < 48)
        fore = (seen >= 0) & ~back[np.clip(seen, 0, None)]
        islands, count = ndimage.label(fore, structure=np.ones((3, 3)))

        def island_of(i):
            ids = islands[seen == i]
            ids = ids[ids > 0]
            return int(np.bincount(ids).argmax()) if len(ids) else 0

        isl = np.array([island_of(i) if not back[i] and area[i] > 0 else 0 for i in range(n)])
        island_area = np.bincount(islands.ravel(), minlength=count + 1)

        # Each shape's neighbours, by how long a border they share.
        pairs = {}
        for a, b in ((seen[:, :-1], seen[:, 1:]), (seen[:-1], seen[1:])):
            m = (a != b) & (a >= 0) & (b >= 0)
            for x, y in zip(a[m], b[m]):
                k = (min(x, y), max(x, y))
                pairs[k] = pairs.get(k, 0) + 1
        near = [dict() for _ in range(n)]
        for (x, y), c in pairs.items():
            near[x][y] = c
            near[y][x] = c

        labels = job.get('labels') or []
        label = np.full(n, -1)
        if labels:
            # The segmenter's scores (its maps run from -10, black, to 6, white): each shape's mean score for each thing.
            maps = np.stack([np.asarray(Image.open(f'mask-{k}.png').convert('L').resize((sw, sh), Image.BILINEAR), dtype=float) / 255 * 16 - 10 for k in range(len(labels))])
            score = np.zeros((n, len(labels)))
            for i in range(n):
                if isl[i]:
                    score[i] = maps[:, seen == i].mean(1)
            small = 0.03 * sw * sh
            votes = {}
            for j in range(1, count + 1):
                members = np.where(isl == j)[0]
                if not len(members):
                    continue
                if island_area[j] < small:
                    # A small island is one thing: its shapes vote, by their area.
                    vote = votes[j] = (score[members] * area[members, None]).sum(0) / max(1, area[members].sum())
                    if vote.max() >= -3.5:
                        label[members] = vote.argmax()
                    continue
                for i in members:
                    s = np.sort(score[i])[::-1]
                    if s[0] >= -2.5 and (len(s) < 2 or s[0] - s[1] >= 0.75):
                        label[i] = score[i].argmax()
            # The undecided take the label of the neighbour they share most border with (in their island).
            for _ in range(20):
                changed = False
                for i in np.where((label < 0) & (isl > 0))[0]:
                    # (Not a large shape the segmenter saw as nothing like it: that is something else, left a part of its own.)
                    best = sorted(((c, k) for k, c in near[i].items() if label[k] >= 0 and isl[k] == isl[i]
                                   and (score[i, label[k]] >= -5.5 or area[i] < 0.002 * sw * sh)), reverse=True)
                    if best:
                        label[i] = label[best[0][1]]
                        changed = True
                if not changed:
                    break
            # A thing with a large piece is one thing (a moon): small islands it took are not it (a star the colour of the moon).
            for k in range(len(labels)):
                pieces = {j: area[(label == k) & (isl == j)].sum() for j in set(isl[label == k])}
                if pieces and max(pieces.values()) >= small:
                    for j, a in pieces.items():
                        if island_area[j] < small and a < 0.15 * max(pieces.values()):
                            label[(label == k) & (isl == j)] = -1
            # Small islands left undecided are like the small ones decided (stars among stars): the most alike in size and colour.
            big = [any(area[(label == k) & (isl == j)].sum() >= small for j in set(isl[label == k])) for k in range(len(labels))]
            decided = [j for j in range(1, count + 1) if island_area[j] < small and (label[isl == j] >= 0).any()
                       and not big[np.bincount(label[(isl == j) & (label >= 0)]).argmax()]]
            for j in range(1, count + 1):
                members = np.where(isl == j)[0]
                if len(members) and island_area[j] < small and (label[members] < 0).all() and decided:
                    def unlike(d):
                        return abs(np.log((island_area[d] + 1) / (island_area[j] + 1))) + 0.5 * np.abs(rgb[isl == d].mean(0) - rgb[members].mean(0)).sum() / 255
                    k = min(decided, key=unlike)
                    if unlike(k) < 1.6:
                        label[members] = np.bincount(label[(isl == k) & (label >= 0)]).argmax()
            # The smallest of many (a tiny star): the many it is most like by the segmenter, when some of them were found.
            many = {int(label[(isl == d) & (label >= 0)][0]) for d in decided}
            for j, vote in votes.items():
                members = np.where(isl == j)[0]
                if (label[members] < 0).all() and int(vote.argmax()) in many and vote.max() >= -5.5:
                    label[members] = vote.argmax()

        # Parts: each label's shapes (one group, or one per island when it is in many: stars), the rest by island.
        def slug(t):
            s = re.sub(r'[^a-z0-9]+', '-', t.lower()).strip('-')
            return (s or 'part')[:32]

        groups = []
        taken = set(['background'])
        def name(t):
            t = slug(t)
            if t[0].isdigit():
                t = 'p-' + t
            base, k = t, 1
            while t in taken:
                k += 1
                t = f'{base}-{k}'
            taken.add(t)
            return t

        def box(members):
            m = np.isin(seen, members)
            ys, xs = np.nonzero(m)
            return [round(float(xs.min()) / scale), round(float(ys.min()) / scale), round(float(xs.max() - xs.min() + 1) / scale), round(float(ys.max() - ys.min() + 1) / scale)] if len(xs) else [0, 0, 0, 0]

        for k, phrase in enumerate(labels):
            members = np.where(label == k)[0]
            if not len(members):
                continue
            by = sorted(set(isl[members]), key=lambda j: (box(members[isl[members] == j])[0]))
            gid = name(job['ids'][k] if job.get('ids') else phrase)
            pieces = [members[isl[members] == j] for j in by] if len(by) > 1 else [members]
            groups.append({'id': gid, 'label': phrase, 'pieces': pieces, 'area': int(area[members].sum())})
        rest = np.where((label < 0) & (isl > 0))[0]
        for j in sorted(set(isl[rest]), key=lambda j: -island_area[j]):
            members = rest[isl[rest] == j]
            groups.append({'id': None, 'label': None, 'pieces': [members], 'area': int(area[members].sum())})
        # Specks (each less than a thousandth) in one group of their own.
        specks = [g for g in groups if g['label'] is None and g['area'] < 0.001 * sw * sh]
        groups = [g for g in groups if g not in specks]
        if specks:
            groups.append({'id': None, 'label': None, 'pieces': [np.concatenate([g['pieces'][0] for g in specks])], 'area': sum(g['area'] for g in specks), 'specks': True})
        for g in groups:
            if g['id'] is None:
                g['id'] = name('specks' if g.get('specks') else 'part')
        for g in groups:
            g['subs'] = [name(f"{g['id']}-{k + 1}") for k in range(len(g['pieces']))] if len(g['pieces']) > 1 else []

        # Shapes as drawn: each with a hairline of its own colour, so no seam shows between neighbours.
        def drawn(i):
            return paths[i].replace('/>', f' stroke="{fills[i]}" stroke-width="{max(1.0, max(W, H) / 700):.2f}" stroke-linejoin="round"/>')

        hexbg = '#%02X%02X%02X' % tuple(int(v) for v in bg)
        out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">',
               f'<rect id="background" width="{W}" height="{H}" fill="{hexbg}"/>']
        # Back to front: what reaches lower in the picture is nearer (a boat's hull dips behind the waves as it rocks), the larger first.
        def depth(g):
            b = box(np.concatenate(g['pieces']))
            return (b[1] + b[3], -g['area'])

        for g in sorted(groups, key=depth):
            if len(g['pieces']) == 1:
                out.append(f'<g id="{g["id"]}">' + ''.join(drawn(i) for i in sorted(g['pieces'][0])) + '</g>')
            else:
                out.append(f'<g id="{g["id"]}">' + ''.join(f'<g id="{sub}">' + ''.join(drawn(i) for i in sorted(p)) + '</g>' for sub, p in zip(g['subs'], g['pieces'])) + '</g>')
        out.append('</svg>')
        open('traced.svg', 'w', encoding='utf-8').write(''.join(out))
        print(json.dumps({'paths': n, 'parts': [{'id': g['id'], 'label': g['label'], 'pieces': len(g['pieces']), 'shapes': int(sum(len(p) for p in g['pieces'])),
                                                'share': round(g['area'] / (sw * sh), 4)} for g in groups]}))
        """;

    /// <summary>
    /// A picture (PNG or JPEG) traced into an SVG of coloured shapes, in its own pixels' units, made into parts: the things
    /// named, found by <paramref name="maps"/> (one grey map each, in their order), or each island of shapes.
    /// </summary>
    public async Task<string> TraceAsync(byte[] picture, string type, TraceStyle style, IReadOnlyList<Thing> things, IReadOnlyList<byte[]> maps, CancellationToken ct)
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
        List<SandboxFile> files = [new("picture." + format, picture)];
        if (things.Count > 0 && maps.Count == things.Count)
        {
            job["labels"] = new JsonArray([.. things.Select(t => (JsonNode)t.Looks)]);
            job["ids"] = new JsonArray([.. things.Select(t => (JsonNode)t.Id)]);
            files.AddRange(maps.Select((m, i) => new SandboxFile($"mask-{i}.png", m)));
        }
        files.Add(new SandboxFile("job.json", Encoding.UTF8.GetBytes(job.ToJsonString())));
        var run = await sandbox.RunAsync(TraceScript, files, 120, ct);
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
