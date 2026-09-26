import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from "react";
import { useNavigate, useParams } from "react-router";
import { api, sendMessage, type Conversation, type Message } from "../api";
import Icon from "../components/Icon";
import Markdown from "../components/Markdown";

/** One rendered turn element: a message from the store, or something still streaming. */
interface Turn {
  key: string;
  role: "user" | "assistant";
  content: string;
  reasoning?: string;
  tools: { id: string; name: string; arguments: string; result?: string; isError?: boolean }[];
  error?: string;
  streaming?: boolean;
}

/** Fold stored messages (user, assistant+tool_calls, tool results, assistant) into display turns. */
function toTurns(messages: Message[]): Turn[] {
  const turns: Turn[] = [];
  for (const m of messages) {
    if (m.role === "user") {
      turns.push({ key: `m${m.id}`, role: "user", content: m.content, tools: [] });
    } else if (m.role === "assistant") {
      const last = turns[turns.length - 1];
      const target: Turn = last && last.role === "assistant" ? last : { key: `m${m.id}`, role: "assistant", content: "", tools: [] };
      if (target !== last) turns.push(target);
      if (m.reasoning) target.reasoning = (target.reasoning ? target.reasoning + "\n\n" : "") + m.reasoning;
      if (m.content) target.content = target.content ? `${target.content}\n\n${m.content}` : m.content;
      for (const c of m.tool_calls ?? []) target.tools.push({ id: c.id, name: c.function.name, arguments: c.function.arguments });
    } else {
      const last = turns[turns.length - 1];
      const call = last?.tools.find((t) => t.id === m.tool_call_id);
      if (call) {
        call.result = m.content;
        call.isError = m.is_error;
      }
    }
  }
  return turns;
}

function ToolCard({ tool }: { tool: Turn["tools"][number] }) {
  let args = tool.arguments;
  try {
    args = JSON.stringify(JSON.parse(tool.arguments), null, 2);
  } catch {
    /* show as sent */
  }
  const state = tool.result === undefined ? "running" : tool.isError ? "failed" : "done";
  return (
    <details className={`tool ${state}`} data-testid="tool-call">
      <summary>
        <Icon name="tool" /> <code>{tool.name}</code> <span className="dim">{state === "running" ? "running…" : state}</span>
      </summary>
      <div className="tool-body">
        <div className="dim small">Arguments</div>
        <pre>{args}</pre>
        {tool.result !== undefined && (
          <>
            <div className="dim small">Result</div>
            <pre>{tool.result}</pre>
          </>
        )}
      </div>
    </details>
  );
}

export default function Chat() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [models, setModels] = useState<string[]>([]);
  const [model, setModel] = useState<string>("");
  const [useTools, setUseTools] = useState(true);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abort = useRef<AbortController | null>(null);
  const bottom = useRef<HTMLDivElement>(null);
  // Set when send() creates a conversation: its URL changes mid-stream, and
  // loading it then would replace the turns being streamed with an empty list.
  const justCreated = useRef<string | null>(null);

  const loadList = useCallback(async () => setConversations(await api.get<Conversation[]>("/api/conversations")), []);

  useEffect(() => {
    loadList().catch((e) => setError(String(e.message ?? e)));
    api
      .get<string[]>("/api/models")
      .then((m) => {
        setModels(m);
        setModel((cur) => cur || m[0] || "");
      })
      .catch(() => setModels([]));
  }, [loadList]);

  useEffect(() => {
    if (!id) {
      setTurns([]);
      return;
    }
    if (id === justCreated.current) {
      justCreated.current = null;
      return;
    }
    api
      .get<Conversation & { messages: Message[] }>(`/api/conversations/${id}`)
      .then((c) => {
        setTurns(toTurns(c.messages));
        if (c.model) setModel(c.model);
      })
      .catch((e) => {
        setError(e.message);
        navigate("/", { replace: true });
      });
  }, [id, navigate]);

  useEffect(() => bottom.current?.scrollIntoView({ block: "end" }), [turns]);

  async function send() {
    const text = draft.trim();
    if (!text || busy) return;
    setError(null);
    setBusy(true);
    let conversationId = id;
    try {
      if (!conversationId) {
        const created = await api.post<Conversation>("/api/conversations", { model: model || null });
        conversationId = created.id;
        justCreated.current = created.id;
        navigate(`/chat/${created.id}`, { replace: true });
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
      return;
    }
    setDraft("");
    const answer: Turn = { key: `s${Date.now()}`, role: "assistant", content: "", tools: [], streaming: true };
    setTurns((t) => [...t, { key: `u${Date.now()}`, role: "user", content: text, tools: [] }, answer]);
    const update = (fn: (t: Turn) => void) =>
      setTurns((all) => {
        const copy = all.slice();
        const last = { ...copy[copy.length - 1], tools: copy[copy.length - 1].tools.map((x) => ({ ...x })) };
        fn(last);
        copy[copy.length - 1] = last;
        return copy;
      });
    abort.current = new AbortController();
    try {
      for await (const ev of sendMessage(conversationId!, text, model || null, useTools, abort.current.signal)) {
        switch (ev.type) {
          case "reasoning":
            update((t) => (t.reasoning = (t.reasoning ?? "") + ev.text));
            break;
          case "content":
            update((t) => (t.content += ev.text));
            break;
          case "tool_call":
            update((t) => t.tools.push({ id: ev.id, name: ev.name, arguments: ev.arguments }));
            break;
          case "tool_result":
            update((t) => {
              const c = t.tools.find((x) => x.id === ev.id);
              if (c) {
                c.result = ev.content;
                c.isError = ev.is_error;
              }
            });
            break;
          case "error":
            update((t) => (t.error = ev.message));
            break;
        }
      }
    } catch (e) {
      if (!(e instanceof DOMException && e.name === "AbortError")) update((t) => (t.error = e instanceof Error ? e.message : String(e)));
    } finally {
      update((t) => (t.streaming = false));
      setBusy(false);
      abort.current = null;
      void loadList();
    }
  }

  function onKey(e: KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void send();
    }
  }

  async function rename(c: Conversation) {
    const title = window.prompt("Rename conversation", c.title);
    if (!title?.trim()) return;
    await api.patch(`/api/conversations/${c.id}`, { title });
    await loadList();
  }

  async function remove(c: Conversation) {
    if (!window.confirm(`Delete “${c.title}”?`)) return;
    await api.del(`/api/conversations/${c.id}`);
    if (c.id === id) navigate("/");
    await loadList();
  }

  return (
    <div className="chat">
      <aside className="conversations" aria-label="Conversations">
        <button className="btn primary wide" onClick={() => navigate("/")}>
          <Icon name="plus" /> New chat
        </button>
        <ul>
          {conversations.map((c) => (
            <li key={c.id} className={c.id === id ? "active" : ""}>
              <button className="conv-title" onClick={() => navigate(`/chat/${c.id}`)} title={c.title}>
                {c.title}
              </button>
              <span className="conv-actions">
                <button className="icon-btn" aria-label={`Rename ${c.title}`} onClick={() => void rename(c)}>
                  <Icon name="edit" size={14} />
                </button>
                <button className="icon-btn" aria-label={`Delete ${c.title}`} onClick={() => void remove(c)}>
                  <Icon name="trash" size={14} />
                </button>
              </span>
            </li>
          ))}
          {conversations.length === 0 && <li className="dim small pad">No conversations yet.</li>}
        </ul>
      </aside>

      <section className="thread">
        <header className="thread-bar">
          <label className="inline">
            Model
            <select value={model} onChange={(e) => setModel(e.target.value)} aria-label="Model">
              {models.length === 0 && <option value="">(gateway unavailable)</option>}
              {models.map((m) => (
                <option key={m}>{m}</option>
              ))}
            </select>
          </label>
          <label className="inline check">
            <input type="checkbox" checked={useTools} onChange={(e) => setUseTools(e.target.checked)} /> Code &amp; docs tools
          </label>
        </header>

        <div className="messages" aria-live="polite">
          {turns.length === 0 && (
            <div className="empty-chat">
              <h2>What are you working on?</h2>
              <p className="dim">
                Ask about your organisation's code — where something is implemented, what breaks if a header changes, which repository a
                change belongs in — or check an API against the installed documentation.
              </p>
            </div>
          )}
          {turns.map((t) => (
            <article key={t.key} className={`turn ${t.role}`} data-testid={`turn-${t.role}`}>
              {t.role === "user" ? (
                <div className="bubble">{t.content}</div>
              ) : (
                <div className="answer">
                  {t.reasoning && (
                    <details className="reasoning">
                      <summary>{t.streaming && !t.content ? "Thinking…" : "Thought process"}</summary>
                      <div className="reasoning-text">{t.reasoning}</div>
                    </details>
                  )}
                  {t.tools.map((tool) => (
                    <ToolCard key={tool.id} tool={tool} />
                  ))}
                  {t.content && <Markdown text={t.content} />}
                  {t.streaming && !t.content && !t.error && <div className="typing" aria-label="Generating" />}
                  {t.error && (
                    <div className="msg bad" role="alert">
                      {t.error}
                    </div>
                  )}
                </div>
              )}
            </article>
          ))}
          <div ref={bottom} />
        </div>

        {error && <div className="msg bad composer-error">{error}</div>}
        <div className="composer">
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onKey}
            placeholder="Message Argus — Enter to send, Shift+Enter for a new line"
            rows={3}
            aria-label="Message"
          />
          {busy ? (
            <button className="btn danger" onClick={() => abort.current?.abort()} aria-label="Stop generating">
              <Icon name="stop" /> Stop
            </button>
          ) : (
            <button className="btn primary" onClick={() => void send()} disabled={!draft.trim()} aria-label="Send">
              <Icon name="send" /> Send
            </button>
          )}
        </div>
      </section>
    </div>
  );
}
