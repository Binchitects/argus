using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using Llm.Core.Data;

namespace Llm.Api.Chat.Tools.Browser;

/// <summary>
/// The browser agent: a real browser (Chromium, in its own container) the model drives to do a task on websites, as the
/// person watches it live in the chat and can take it over. Each call returns the page (its elements, numbered) and a
/// picture of it (to a model that sees). What sends something (a form, a purchase, a post, a deletion) goes through
/// browser_submit, which asks the person first; a password or a card number is never typed by it: the person takes over.
/// </summary>
public sealed partial class BrowserTool(BrowserSessions sessions, BrowserProxy proxy, AppDbContext db) : IChatTool
{
    public const string ToolId = "browser";
    /// <summary>How long a call waits while the person has the browser.</summary>
    private static readonly TimeSpan TakenOverWait = TimeSpan.FromMinutes(10);

    public string Id => ToolId;
    public string Title => "Browser";
    public string Description => "Uses a real browser to do things on websites while you watch: opens pages, clicks, types and reads; asks before sending anything.";
    public string Icon => "globe";

    /// <summary>Off in new chats: turned on in a chat when it should use the browser.</summary>
    public ToolSetting Defaults() => new() { ToolId = Id, OnByDefault = false };

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        await sessions.UnavailableAsync(ct) ?? (proxy.Sites.Trim().Length == 0 ? "An admin has not chosen which sites the browser may open (Settings → Tools)." : null);

    private const string Instructions =
        "You have a real web browser (browser_* functions). The person watches its screen live in the chat and can take it over.\n" +
        "- Each call returns the page: its title, address, the elements you can use as [n] numbers, and the start of its text; " +
        "a picture of the screen comes with it when you can see pictures. Use the numbers of the latest call only.\n" +
        "- browser_click follows links, opens menus, ticks boxes; anything that SENDS or CHANGES something on the site " +
        "(submitting a form, buying, booking, posting, sending, deleting, confirming, signing up) is browser_submit, which asks the person first.\n" +
        "- Never type a password, a card number or another secret, and never ask the person for one: ask them to take over " +
        "(the Take over button above the box) to do that part themselves, then go on.\n" +
        "- Say what you found or did, with the page's address. Stop and ask when the task is unclear or a page asks for something you do not know.";

    private static readonly JsonArray Functions =
    [
        Schema.Function("browser_open", "Opens an address in the browser (http or https) and returns the page.", new JsonObject { ["url"] = Schema.Text("The address, e.g. https://example.com/page.") }, "url"),
        Schema.Function("browser_click", "Clicks element [n] of the page (a link, a button that does not send anything, a tab, a box) and returns the page after.",
            new JsonObject { ["element"] = new JsonObject { ["type"] = "integer", ["description"] = "The element's number, from the latest page." } }, "element"),
        Schema.Function("browser_type", "Types text into field [n] (replacing what is in it). With search=true it then presses Enter, for a search box only.",
            new JsonObject
            {
                ["element"] = new JsonObject { ["type"] = "integer", ["description"] = "The field's number." }, ["text"] = Schema.Text("What to type."),
                ["search"] = new JsonObject { ["type"] = "boolean", ["description"] = "Press Enter after (search boxes only)." },
            }, "element", "text"),
        Schema.Function("browser_submit", "Sends something: presses button [n] (submit, buy, post, send, delete, confirm...), or with text, types it into field [n] and presses Enter. Asks the person first.",
            new JsonObject { ["element"] = new JsonObject { ["type"] = "integer", ["description"] = "The button's or field's number." }, ["text"] = Schema.Text("Text to type first, for a field.") }, "element"),
        Schema.Function("browser_press", "Presses a key: Escape, Tab, ArrowDown, ArrowUp, ArrowLeft, ArrowRight, PageDown, PageUp, Home, End or Backspace.",
            new JsonObject { ["key"] = Schema.Text("The key.") }, "key"),
        Schema.Function("browser_scroll", "Scrolls the page down or up by a screen and returns it.", new JsonObject { ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("down", "up") } }, "direction"),
        Schema.Function("browser_back", "Goes back to the page before.", []),
        Schema.Function("browser_read", "Returns the whole text of the page (up to 30,000 characters), to read an article or a long list.", []),
    ];

    /// <summary>The page: its interactive elements numbered (data-arena-ref), and the start of its text.</summary>
    private const string SnapshotScript = """
        (() => {
          document.querySelectorAll('[data-arena-ref]').forEach(e => e.removeAttribute('data-arena-ref'));
          const q = 'a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=radio],[role=tab],[role=menuitem],[role=option],[role=switch],[role=searchbox],[role=textbox],[role=combobox],[contenteditable=""],[contenteditable=true],[onclick]';
          const vw = innerWidth, vh = innerHeight, out = [];
          let n = 0;
          for (const e of document.querySelectorAll(q)) {
            const r = e.getBoundingClientRect(), s = getComputedStyle(e);
            if (r.width < 2 || r.height < 2 || s.visibility === 'hidden' || s.display === 'none' || e.disabled || e.getAttribute('aria-hidden') === 'true') continue;
            const on = r.bottom > 0 && r.top < vh && r.right > 0 && r.left < vw;
            if (!on && out.length >= 80) continue;
            const ref = ++n;
            e.setAttribute('data-arena-ref', String(ref));
            const tag = e.tagName.toLowerCase(), type = (e.getAttribute('type') || '').toLowerCase();
            const name = (e.getAttribute('aria-label') || (tag === 'input' ? '' : e.innerText) || e.getAttribute('placeholder') || e.getAttribute('title') || e.getAttribute('alt') || e.getAttribute('name') || (type === 'submit' ? e.value : '') || '').trim().replace(/\s+/g, ' ').slice(0, 80);
            const role = e.getAttribute('role') || (tag === 'a' ? 'link' : tag === 'input' ? (type || 'text') : tag);
            const value = (tag === 'input' || tag === 'textarea') && type !== 'password' && type !== 'submit' ? (e.value || '').slice(0, 60) : (tag === 'select' ? (e.selectedOptions[0]?.text || '') : '');
            out.push({ ref, role, name, on, value, checked: e.checked === true });
            if (out.length >= 150) break;
          }
          const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n').trim();
          return { url: location.href, title: document.title, elements: out, text: text.slice(0, 2500), more: text.length > 2500, scroll: Math.round(scrollY), height: document.documentElement.scrollHeight, view: vh };
        })()
        """;

    /// <summary>Element [n], brought into view: where it is, and what it is (a sending button? a secret field? a search box?).</summary>
    private static string ElementScript(int element) => $$"""
        (() => {
          const e = document.querySelector('[data-arena-ref="{{element}}"]');
          if (!e) return null;
          e.scrollIntoView({ block: 'center', inline: 'center' });
          const r = e.getBoundingClientRect(), tag = e.tagName.toLowerCase(), type = (e.getAttribute('type') || '').toLowerCase();
          const ac = (e.getAttribute('autocomplete') || '').toLowerCase(), names = ((e.getAttribute('name') || '') + ' ' + (e.id || '') + ' ' + (e.getAttribute('aria-label') || '')).toLowerCase();
          return {
            x: r.left + r.width / 2, y: r.top + r.height / 2, tag, type, inForm: !!e.closest('form'),
            text: (e.innerText || e.value || e.getAttribute('aria-label') || e.getAttribute('title') || '').trim().replace(/\s+/g, ' ').slice(0, 80),
            field: tag === 'textarea' || tag === 'select' || e.isContentEditable || (tag === 'input' && !['button', 'submit', 'reset', 'checkbox', 'radio', 'image', 'file'].includes(type)) || ['textbox', 'searchbox', 'combobox'].includes(e.getAttribute('role')),
            secret: type === 'password' || ac.includes('password') || ac.startsWith('cc-') || /card.?num|cvc|cvv|security.?code|iban|ssn|passw/.test(names),
            search: type === 'search' || e.getAttribute('role') === 'searchbox' || !!e.closest('[role=search]') || /(^|[\s_-])(q|query|search|keywords?)([\s_-]|$)/.test(names),
          };
        })()
        """;

    /// <summary>Whether pressing it sends or changes something: a form's submit button, or words that say so.</summary>
    public static bool Sends(JsonObject element)
    {
        var tag = element["tag"]?.GetValue<string>() ?? "";
        var type = element["type"]?.GetValue<string>() ?? "";
        var inForm = element["inForm"]?.GetValue<bool>() == true;
        var text = element["text"]?.GetValue<string>() ?? "";
        return (tag == "button" && inForm && type is not ("button" or "reset")) || (tag == "input" && type is "submit" or "image") || SendingWords().IsMatch(text);
    }

    [GeneratedRegex(@"\b(buy|pay|purchase|order|checkout|check out|place|submit|send|post|publish|tweet|reply|delete|remove|confirm|sign ?up|register|subscribe|transfer|book|reserve|apply|donate|unsubscribe|cancel (my )?(order|subscription|account))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SendingWords();

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(Functions, Instructions, (name, args, token) => CallAsync(context, name, args, token))
    {
        AsksFirst = function => function == "browser_submit",
    });

    private async Task<ToolResult> CallAsync(ToolContext context, string name, JsonObject args, CancellationToken ct)
    {
        BrowserSession s;
        try
        {
            s = await sessions.OpenAsync(context.Conversation.Id, context.User.Id, ct);
        }
        catch (CdpException ex)
        {
            return new ToolResult($"The browser could not be reached: {ex.Message}", IsError: true);
        }
        // The person has it: the agent waits until they hand it back.
        var waited = false;
        var until = DateTimeOffset.UtcNow + TakenOverWait;
        while (s.TakenOver && DateTimeOffset.UtcNow < until)
        {
            waited = true;
            await Task.Delay(1000, ct);
        }
        if (s.TakenOver)
        {
            return new ToolResult("The person still has the browser: ask them what to do next.", IsError: true);
        }
        await s.Turn.WaitAsync(ct);
        try
        {
            var note = waited ? "The person had the browser and handed it back; this is where it is now.\n" : "";
            var result = name switch
            {
                "browser_open" => await OpenAsync(context, s, Schema.Str(args, "url") ?? "", ct),
                "browser_click" => await ClickAsync(context, s, Number(args), submit: false, null, ct),
                "browser_submit" => await ClickAsync(context, s, Number(args), submit: true, Schema.Str(args, "text"), ct),
                "browser_type" => await TypeAsync(context, s, Number(args), Schema.Str(args, "text") ?? "", args["search"]?.GetValue<bool>() == true, ct),
                "browser_press" => await PressAsync(context, s, Schema.Str(args, "key") ?? "", ct),
                "browser_scroll" => await ScrollAsync(context, s, Schema.Str(args, "direction") == "up" ? -1 : 1, ct),
                "browser_back" => await BackAsync(context, s, ct),
                "browser_read" => await ReadAsync(s, ct),
                _ => new ToolResult($"There is no {name}.", IsError: true),
            };
            return note.Length > 0 ? result with { Text = note + result.Text } : result;
        }
        catch (CdpException ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
        finally
        {
            s.Turn.Release();
        }
    }

    private static int Number(JsonObject args) => args["element"] is JsonValue v && (v.TryGetValue<int>(out var n) || (v.TryGetValue<string>(out var t) && int.TryParse(t.Trim('[', ']'), out n))) ? n : 0;

    private async Task<ToolResult> OpenAsync(ToolContext context, BrowserSession s, string address, CancellationToken ct)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return new ToolResult("Give an http or https address.", IsError: true);
        }
        var (address4, refusal) = await proxy.CheckAsync(url.Host, url.Port, ct);
        if (address4 is null)
        {
            return new ToolResult(refusal!, IsError: true);
        }
        await sessions.NavigateAsync(s, url.AbsoluteUri, ct);
        return await PageAsync(context, s, "", ct);
    }

    private async Task<ToolResult> ClickAsync(ToolContext context, BrowserSession s, int element, bool submit, string? text, CancellationToken ct)
    {
        if (await sessions.EvaluateAsync(s, ElementScript(element), ct) is not JsonObject e)
        {
            return new ToolResult($"The page has no element [{element}] now: use the numbers of the latest page.", IsError: true);
        }
        if (text is not null)
        {
            if (e["secret"]?.GetValue<bool>() == true)
            {
                return Secret();
            }
            await FillAsync(s, e, text, ct);
            await sessions.KeyAsync(s, "Enter", ct);
            await sessions.SettleAsync(s, ct);
            return await PageAsync(context, s, $"Typed into [{element}] and pressed Enter.\n", ct);
        }
        if (!submit && Sends(e))
        {
            return new ToolResult($"[{element}] \"{e["text"]}\" sends or changes something on the site: use browser_submit, which asks the person first.", IsError: true);
        }
        await sessions.ClickAsync(s, e["x"]!.GetValue<double>(), e["y"]!.GetValue<double>(), ct);
        await sessions.SettleAsync(s, ct);
        return await PageAsync(context, s, $"Clicked [{element}] \"{e["text"]}\".\n", ct);
    }

    private async Task<ToolResult> TypeAsync(ToolContext context, BrowserSession s, int element, string text, bool search, CancellationToken ct)
    {
        if (await sessions.EvaluateAsync(s, ElementScript(element), ct) is not JsonObject e)
        {
            return new ToolResult($"The page has no element [{element}] now: use the numbers of the latest page.", IsError: true);
        }
        if (e["secret"]?.GetValue<bool>() == true)
        {
            return Secret();
        }
        if (e["field"]?.GetValue<bool>() != true)
        {
            return new ToolResult($"[{element}] is not a field to type into.", IsError: true);
        }
        await FillAsync(s, e, text, ct);
        if (search)
        {
            if (e["search"]?.GetValue<bool>() != true)
            {
                return await PageAsync(context, s, $"Typed into [{element}], but did not press Enter: it is not a search box. To send it, use browser_submit.\n", ct);
            }
            await sessions.KeyAsync(s, "Enter", ct);
            await sessions.SettleAsync(s, ct);
        }
        return await PageAsync(context, s, $"Typed into [{element}]{(search ? " and searched" : "")}.\n", ct);
    }

    private static ToolResult Secret() => new(
        "That field is for a password, a card number or another secret: never type one. Ask the person to take over the browser (Take over, above the box) to fill it in themselves, then go on.",
        IsError: true);

    /// <summary>Clicks the field, empties it and types the text.</summary>
    private async Task FillAsync(BrowserSession s, JsonObject e, string text, CancellationToken ct)
    {
        await sessions.ClickAsync(s, e["x"]!.GetValue<double>(), e["y"]!.GetValue<double>(), ct);
        await sessions.EvaluateAsync(s, """
            (() => { const e = document.activeElement; if (!e) return false;
              if ('value' in e && e.tagName !== 'SELECT') { e.value = ''; e.dispatchEvent(new Event('input', { bubbles: true })); }
              else if (e.isContentEditable) { e.textContent = ''; }
              return true; })()
            """, ct);
        await sessions.TypeAsync(s, text, ct);
    }

    private async Task<ToolResult> PressAsync(ToolContext context, BrowserSession s, string key, CancellationToken ct)
    {
        if (key.Equals("Enter", StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult("Enter may send a form: use browser_type with search=true for a search box, or browser_submit to send.", IsError: true);
        }
        await sessions.KeyAsync(s, key, ct);
        await sessions.SettleAsync(s, ct);
        return await PageAsync(context, s, $"Pressed {key}.\n", ct);
    }

    private async Task<ToolResult> ScrollAsync(ToolContext context, BrowserSession s, int direction, CancellationToken ct)
    {
        await sessions.EvaluateAsync(s, $"window.scrollBy(0, {direction} * Math.round(innerHeight * 0.85))", ct);
        await Task.Delay(300, ct);
        return await PageAsync(context, s, direction > 0 ? "Scrolled down.\n" : "Scrolled up.\n", ct);
    }

    private async Task<ToolResult> BackAsync(ToolContext context, BrowserSession s, CancellationToken ct)
    {
        await sessions.EvaluateAsync(s, "history.back()", ct);
        await sessions.SettleAsync(s, ct);
        return await PageAsync(context, s, "Went back.\n", ct);
    }

    private async Task<ToolResult> ReadAsync(BrowserSession s, CancellationToken ct)
    {
        var text = (await sessions.EvaluateAsync(s, "(document.body ? document.body.innerText : '')", ct))?.GetValue<string>() ?? "";
        return new ToolResult($"{s.Title} — {s.Url}\n\n" + (text.Length > 30_000 ? text[..30_000] + "\n… (cut)" : text));
    }

    /// <summary>The page after a step, in words for the model, and its picture (shown in the chat; seen by a model that sees).</summary>
    private async Task<ToolResult> PageAsync(ToolContext context, BrowserSession s, string done, CancellationToken ct)
    {
        var page = await sessions.EvaluateAsync(s, SnapshotScript, ct) as JsonObject ?? [];
        s.Url = page["url"]?.GetValue<string>() ?? s.Url;
        s.Title = page["title"]?.GetValue<string>() ?? "";
        var text = new StringBuilder(done)
            .Append(CultureInfo.InvariantCulture, $"Page: {s.Title} — {s.Url}")
            .Append(CultureInfo.InvariantCulture, $" (scrolled {page["scroll"]} of {page["height"]} px, the screen {page["view"]} px high)\n");
        var elements = (page["elements"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        text.Append(elements.Count == 0 ? "No elements to use on screen.\n" : "Elements on screen:\n");
        foreach (var on in new[] { true, false })
        {
            var group = elements.Where(e => e["on"]?.GetValue<bool>() == on).ToList();
            if (!on && group.Count > 0)
            {
                text.Append("Further down or up (scroll, or use them; they are brought into view):\n");
            }
            foreach (var e in group)
            {
                text.Append(CultureInfo.InvariantCulture, $"[{e["ref"]}] {e["role"]} \"{e["name"]}\"");
                if (e["value"]?.GetValue<string>() is { Length: > 0 } value)
                {
                    text.Append(CultureInfo.InvariantCulture, $" = \"{value}\"");
                }
                if (e["checked"]?.GetValue<bool>() == true)
                {
                    text.Append(" (checked)");
                }
                text.Append('\n');
            }
        }
        text.Append("\nIts text:\n").Append(page["text"]?.GetValue<string>() ?? "");
        if (page["more"]?.GetValue<bool>() == true)
        {
            text.Append("\n… (browser_read for all of it)");
        }
        var jpeg = await sessions.ScreenAsync(s, ct);
        var shot = new ChatAttachment
        {
            UserId = context.User.Id, FileName = $"browser-{DateTime.UtcNow:HHmmss}.jpg", ContentType = "image/jpeg", Size = jpeg.Length, Kind = "image", Data = jpeg, Text = "", Origin = "tool",
        };
        db.ChatAttachments.Add(shot);
        await db.SaveChangesAsync(ct);
        return new ToolResult(text.ToString(), Files: [shot]) { SeeImages = true };
    }
}
