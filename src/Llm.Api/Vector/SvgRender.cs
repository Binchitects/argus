using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat.Tools;
using Llm.Api.Chat.Tools.Browser;

namespace Llm.Api.Vector;

/// <summary>A part of a drawing (an element with an id): where it is in the drawing's own units, and how it looks.</summary>
public sealed record PartBox(string Id, string Tag, double X, double Y, double Width, double Height, string? Fill, string? Text);

/// <summary>
/// SVG drawn as pictures: by the stack's Chromium (the browser module) as a browser shows it, at any moment of its
/// animations (SMIL and CSS, paused and sought there), frame after frame for a video, and where each of its parts is;
/// without it, a still picture by resvg in the sandbox (as it is before anything moves). Every drawing is made safe first.
/// </summary>
public sealed class SvgRender(BrowserSessions browser, SandboxClient sandbox)
{
    /// <summary>The largest picture drawn, a side; and frames at most (a minute at 30 a second).</summary>
    public const int MostSide = 4096;
    public const int MostFrames = 1800;

    public async Task<bool> MovesAsync(CancellationToken ct) => await browser.UnavailableAsync(ct) is null;

    private static string Page(string markup, int width, int height) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{{margin:0;padding:0;background:transparent;overflow:hidden}}"
        + $"svg{{display:block;width:{width}px;height:{height}px}}</style></head><body>{markup}</body></html>";

    /// <summary>Its animations held at <c>t</c> seconds (SMIL's and CSS's), and two frames drawn so a picture shows that moment.</summary>
    private const string Seek = """
        (async (t) => {
          const s = document.querySelector('svg');
          if (s && s.pauseAnimations) { s.pauseAnimations(); s.setCurrentTime(t); }
          for (const a of document.getAnimations()) { a.pause(); a.currentTime = t * 1000; }
          await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
          return true;
        })
        """;

    private static async Task LoadAsync(Func<string, JsonObject?, Task<JsonObject>> send, string markup, int width, int height)
    {
        var tree = await send("Page.getFrameTree", null);
        await send("Page.setDocumentContent", new JsonObject { ["frameId"] = tree["frameTree"]!["frame"]!["id"]!.GetValue<string>(), ["html"] = Page(markup, width, height) });
        // Its fonts in.
        await send("Runtime.evaluate", new JsonObject { ["expression"] = "document.fonts.ready.then(() => true)", ["awaitPromise"] = true, ["returnByValue"] = true });
    }

    private static async Task<byte[]> ShotAsync(Func<string, JsonObject?, Task<JsonObject>> send, double t, int width, int height)
    {
        await send("Runtime.evaluate", new JsonObject { ["expression"] = $"{Seek}({t.ToString(CultureInfo.InvariantCulture)})", ["awaitPromise"] = true, ["returnByValue"] = true });
        var shot = await send("Page.captureScreenshot", new JsonObject
        {
            ["format"] = "png", ["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = height, ["scale"] = 1 },
        });
        return Convert.FromBase64String(shot["data"]!.GetValue<string>());
    }

    /// <summary>The size a drawing is drawn at: its own shape, the longer side <paramref name="side"/> pixels.</summary>
    public static (int Width, int Height) SizeFor(SafeSvg svg, int side)
    {
        side = Math.Clamp(side, 16, MostSide);
        var scale = side / Math.Max(svg.Width, svg.Height);
        return (Math.Max(1, (int)Math.Round(svg.Width * scale)), Math.Max(1, (int)Math.Round(svg.Height * scale)));
    }

    /// <summary>A PNG of the drawing at <paramref name="at"/> seconds, its longer side <paramref name="side"/> pixels, its background see-through.</summary>
    public async Task<byte[]> PngAsync(string markup, int side, double at, CancellationToken ct)
    {
        var svg = SvgSafe.Clean(markup);
        var (w, h) = SizeFor(svg, side);
        if (await browser.UnavailableAsync(ct) is null)
        {
            return await browser.DrawingPageAsync(w, h, async send =>
            {
                await LoadAsync(send, svg.Markup, w, h);
                return await ShotAsync(send, Math.Max(0, at), w, h);
            }, ct);
        }
        return await ResvgAsync(svg.Markup, w, h, ct);
    }

    private const string ResvgScript = """
        import json, resvg_py
        job = json.load(open('job.json'))
        png = resvg_py.svg_to_bytes(svg_string=open('drawing.svg', encoding='utf-8').read(), width=job['width'], height=job['height'])
        open('drawing.png', 'wb').write(bytes(png))
        print(json.dumps({'ok': True}))
        """;

    /// <summary>A still picture by resvg in the sandbox: as the drawing is before anything moves.</summary>
    private async Task<byte[]> ResvgAsync(string markup, int width, int height, CancellationToken ct)
    {
        if (sandbox.Unavailable() is { } why)
        {
            throw new SvgException($"Neither the browser nor the sandbox runs here, so the drawing cannot be drawn as a picture ({why}).");
        }
        var run = await sandbox.RunAsync(ResvgScript, [
            new SandboxFile("drawing.svg", Encoding.UTF8.GetBytes(markup)),
            new SandboxFile("job.json", Encoding.UTF8.GetBytes(new JsonObject { ["width"] = width, ["height"] = height }.ToJsonString())),
        ], 60, ct);
        return run.Files.FirstOrDefault(f => f.Name == "drawing.png")?.Bytes
            ?? throw new SvgException($"The drawing could not be drawn: {(run.Stderr.Length > 0 ? run.Stderr[^Math.Min(300, run.Stderr.Length)..] : run.Error ?? "no picture came back")}");
    }

    /// <summary>
    /// The drawing's frames, <paramref name="fps"/> a second for <paramref name="seconds"/>, its longer side <paramref name="side"/>
    /// pixels: by the browser only (the sandbox's renderer does not move).
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> FramesAsync(string markup, int side, double seconds, int fps, CancellationToken ct)
    {
        if (await browser.UnavailableAsync(ct) is { } why)
        {
            throw new SvgException($"Animations are recorded by the browser module, which does not run here: {why}");
        }
        var svg = SvgSafe.Clean(markup);
        var (w, h) = SizeFor(svg, side);
        var count = Math.Clamp((int)Math.Round(seconds * fps), 1, MostFrames);
        return await browser.DrawingPageAsync(w, h, async send =>
        {
            await LoadAsync(send, svg.Markup, w, h);
            var frames = new List<byte[]>(count);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                frames.Add(await ShotAsync(send, i / (double)fps, w, h));
            }
            return frames;
        }, ct);
    }

    /// <summary>Where each part with an id is (in the drawing's own units, as drawn at the start), for a model to place motions and changes.</summary>
    private const string Measure = """
        (() => {
          const s = document.querySelector('svg');
          const vb = s.viewBox.baseVal;
          const r = s.getBoundingClientRect();
          const kx = vb && vb.width ? vb.width / r.width : 1, ky = vb && vb.height ? vb.height / r.height : 1;
          const ox = vb ? vb.x : 0, oy = vb ? vb.y : 0;
          const drawn = new Set(['g','path','rect','circle','ellipse','line','polyline','polygon','text','use','image','svg']);
          const out = [];
          for (const e of s.querySelectorAll('[id]')) {
            if (!drawn.has(e.localName) || e.closest('defs,clipPath,mask,pattern,symbol,marker')) continue;
            const b = e.getBoundingClientRect();
            if (!b.width && !b.height) continue;
            out.push({ id: e.id, tag: e.localName, x: (b.left - r.left) * kx + ox, y: (b.top - r.top) * ky + oy, w: b.width * kx, h: b.height * ky,
              fill: e.getAttribute('fill') || (e.querySelector('[fill]') && e.querySelector('[fill]').getAttribute('fill')) || null,
              text: e.localName === 'text' ? e.textContent.trim().slice(0, 60) : null });
            if (out.length >= 400) break;
          }
          return out;
        })()
        """;

    public async Task<IReadOnlyList<PartBox>> PartsAsync(string markup, CancellationToken ct)
    {
        if (await browser.UnavailableAsync(ct) is not null)
        {
            return [];
        }
        var svg = SvgSafe.Clean(markup);
        var (w, h) = SizeFor(svg, 1024);
        return await browser.DrawingPageAsync(w, h, async send =>
        {
            await LoadAsync(send, SvgMotion.Still(System.Xml.Linq.XElement.Parse(svg.Markup)).ToString(System.Xml.Linq.SaveOptions.DisableFormatting), w, h);
            var r = await send("Runtime.evaluate", new JsonObject { ["expression"] = Measure, ["returnByValue"] = true });
            var list = r["result"]?["value"] as JsonArray ?? [];
            return (IReadOnlyList<PartBox>)[.. list.OfType<JsonObject>().Select(p => new PartBox(
                p["id"]!.GetValue<string>(), p["tag"]!.GetValue<string>(), Round(p["x"]), Round(p["y"]), Round(p["w"]), Round(p["h"]),
                p["fill"]?.GetValue<string>(), p["text"]?.GetValue<string>()))];
        }, ct);
    }

    private static double Round(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Round(d, 1) : 0;

    /// <summary>The parts, a line each, for a model: "#wheel-left circle at 140,230 size 28×28 fill #222".</summary>
    public static string Describe(IReadOnlyList<PartBox> parts) =>
        string.Join('\n', parts.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"#{p.Id} {p.Tag} at {p.X:0.#},{p.Y:0.#} size {p.Width:0.#}×{p.Height:0.#}{(p.Fill is { } f ? $" fill {f}" : "")}{(p.Text is { Length: > 0 } t ? $" text \"{t}\"" : "")}")));

    public static string Json(IReadOnlyList<PartBox> parts) => JsonSerializer.Serialize(parts, JsonSerializerOptions.Web);
}
