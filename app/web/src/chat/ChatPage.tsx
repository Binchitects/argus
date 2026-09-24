import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { api, ApiError } from '../api'
import { ErrorText } from '../components/ErrorText'
import { formatValue } from '../format'
import { streamChat, upload, type Attachment, type ChatConfig, type ChatEvent, type Conversation, type ConversationSummary, type Message } from './api'
import { AssistantTurn, UserTurn, type Notice } from './Turn'

const blank = (id: string, role: Message['role'], content = ''): Message => ({
  id, role, content, reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [],
  status: 'complete', error: null, promptTokens: null, cachedTokens: null, completionTokens: null, noAccess: false,
})

/** Messages grouped as the page shows them: a question, then everything that answered it. */
function group(messages: Message[]): { user?: Message; answer: Message[] }[] {
  const out: { user?: Message; answer: Message[] }[] = []
  for (const m of messages) {
    if (m.role === 'user') out.push({ user: m, answer: [] })
    else if (out.length) out[out.length - 1].answer.push(m)
    else out.push({ answer: [m] })
  }
  return out
}

export function ChatPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const config = useQuery({ queryKey: ['chat', 'config'], queryFn: () => api<ChatConfig>('/api/chat/config'), staleTime: 60_000 })
  const [search, setSearch] = useState('')
  const list = useQuery({
    queryKey: ['chat', 'list', search],
    queryFn: () => api<ConversationSummary[]>(`/api/chat/conversations${search ? `?q=${encodeURIComponent(search)}` : ''}`),
  })
  const [listOpen, setListOpen] = useState(false)
  // A new chat gets its id with its first message; the thread must survive that
  // navigation (it is mid-answer), so it is keyed by "which new chat" until then.
  const [fresh, setFresh] = useState(0)
  const [adopted, setAdopted] = useState<string | null>(null)
  const threadKey = !id || id === adopted ? `new-${fresh}` : id

  return (
    <div className="chat-layout">
      <aside className={`chat-list${listOpen ? ' open' : ''}`} aria-label="Chats">
        {/* On a phone the open list covers the page, its opener included. */}
        <button type="button" className="link chat-list-close" onClick={() => setListOpen(false)}>
          Hide chats
        </button>
        <button type="button" className="button" onClick={() => { setFresh((n) => n + 1); setAdopted(null); navigate('/chat'); setListOpen(false) }}>
          New chat
        </button>
        <input type="search" placeholder="Search chats" aria-label="Search chats" value={search} onChange={(e) => setSearch(e.target.value)} />
        <ul className="plain chat-items">
          {list.data?.map((c) => (
            <li key={c.id}>
              <Link to={`/chat/${c.id}`} className={c.id === id ? 'active' : ''} onClick={() => setListOpen(false)}>
                {c.title}
              </Link>
            </li>
          ))}
          {list.data?.length === 0 && <li className="muted">{search ? 'No chat matches.' : 'No chats yet.'}</li>}
        </ul>
      </aside>
      <section className="chat-main">
        <button type="button" className="link chat-list-toggle" onClick={() => setListOpen(true)} aria-expanded={listOpen}>
          Chats
        </button>
        {config.data ? (
          <Thread
            key={threadKey}
            id={id}
            config={config.data}
            onAdopt={setAdopted}
            onChanged={() => queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })}
          />
        ) : (
          <ErrorText error={config.error} />
        )}
      </section>
    </div>
  )
}

function Thread({ id, config, onChanged, onAdopt }: { id?: string; config: ChatConfig; onChanged: () => void; onAdopt: (id: string) => void }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const loaded = useQuery({ queryKey: ['chat', 'conversation', id], queryFn: () => api<Conversation>(`/api/chat/conversations/${id}`), enabled: !!id })
  // While answering (and until the saved copy is back), the page shows the live copy.
  const [live, setLive] = useState<Message[] | null>(null)
  const messages = live ?? loaded.data?.messages ?? []
  const [titleEdit, setTitleEdit] = useState<string | null>(null)
  const [liveTitle, setLiveTitle] = useState<string | null>(null)
  const title = titleEdit ?? liveTitle ?? loaded.data?.title ?? 'New chat'
  const [thinkingChoice, setThinking] = useState<string | null>(null)
  const thinking = thinkingChoice ?? loaded.data?.thinking ?? ''
  const [argusChoice, setUseArgus] = useState<boolean | null>(null)
  const useArgus = argusChoice ?? loaded.data?.useArgus ?? true
  const [notices, setNotices] = useState<Notice[]>([])
  const [streaming, setStreaming] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement>(null)
  const liveRef = useRef<Message[] | null>(null)
  useEffect(() => {
    liveRef.current = live
  }, [live])

  // Braces matter: newer Chrome returns a Promise from scrollIntoView, and an effect
  // that returns anything but a function crashes React on cleanup (measured).
  useEffect(() => {
    bottom.current?.scrollIntoView?.({ block: 'end' })
  }, [messages.length, streaming])

  const onEvent = useCallback((e: ChatEvent) => {
    setLive((ms) => {
      const copy = [...(ms ?? [])]
      const lastAssistant = () => {
        for (let i = copy.length - 1; i >= 0; i--) if (copy[i].role === 'assistant') return (copy[i] = { ...copy[i] })
        return undefined
      }
      switch (e.type) {
        case 'assistant':
          copy.push(blank(e.id, 'assistant'))
          break
        case 'reasoning': {
          const a = lastAssistant()
          if (a) a.reasoning = (a.reasoning ?? '') + e.text
          break
        }
        case 'content': {
          const a = lastAssistant()
          if (a) a.content += e.text
          break
        }
        case 'usage': {
          const a = lastAssistant()
          if (a) Object.assign(a, { promptTokens: e.prompt, cachedTokens: e.cached, completionTokens: e.completion })
          break
        }
        case 'tool_call': {
          const a = lastAssistant()
          if (a) a.toolCalls = [...(a.toolCalls ?? []), { id: e.id, function: { name: e.name, arguments: e.arguments } }]
          break
        }
        case 'tool_result':
          copy.push({ ...blank(`${e.id}-result`, 'tool', e.text), toolCallId: e.id, toolName: e.name, noAccess: e.noAccess, status: e.isError ? 'failed' : 'complete' })
          break
        case 'error': {
          const a = lastAssistant()
          if (a) Object.assign(a, { status: 'failed', error: e.message })
          break
        }
      }
      return copy
    })
    if (e.type === 'title') setLiveTitle(e.title)
    if (e.type === 'notice') setNotices((n) => [...n, { kind: e.kind, text: e.text }])
  }, [])

  /** Answers; false when the question never reached the server, so it is not saved. */
  const run = useCallback(
    async (conversationId: string, path: string, body: object, start: Message[]): Promise<boolean> => {
      setLive(start)
      setStreaming(true)
      setError(null)
      setNotices([])
      const controller = new AbortController()
      abort.current = controller
      let stopped = false
      let received = false
      try {
        await streamChat(`/api/chat/conversations/${conversationId}/${path}`, body, (e) => {
          received = true
          onEvent(e)
        }, controller.signal)
      } catch (err) {
        stopped = err instanceof DOMException && err.name === 'AbortError'
        if (!stopped) {
          setError(err instanceof ApiError ? err.message
            : received ? 'The answer was interrupted.'
            : 'The message did not reach the server. It is back in the box below: send it again.')
        }
      } finally {
        setStreaming(false)
        abort.current = null
        if (stopped) {
          setLive((ms) => ms?.map((m, i) => (i === ms.length - 1 && m.role === 'assistant' ? { ...m, status: 'stopped' } : m)) ?? ms)
        }
        onChanged()
        // The server's copy is the truth: ids, statuses, and what a stop kept. After
        // a stop the server saves a moment AFTER the stream ends, so wait until its
        // copy has the last answer before replacing what is on screen.
        const lastId = [...(liveRef.current ?? [])].reverse().find((m) => m.role === 'assistant')?.id
        for (let i = 0; i < 20; i++) {
          await queryClient.invalidateQueries({ queryKey: ['chat', 'conversation', conversationId] })
          const saved = queryClient.getQueryData<Conversation>(['chat', 'conversation', conversationId])
          if (!lastId || saved?.messages.some((m) => m.id === lastId)) {
            setLive(null)
            break
          }
          await new Promise((r) => setTimeout(r, 300))
        }
      }
      return received || stopped
    },
    [onEvent, onChanged, queryClient],
  )

  const send = async (text: string, files: Attachment[]): Promise<boolean> => {
    let conversationId = id
    if (!conversationId) {
      try {
        const created = await api<Conversation>('/api/chat/conversations', { body: { thinking: thinking || null, useArgus } })
        conversationId = created.id
        queryClient.setQueryData(['chat', 'conversation', created.id], { ...created, messages: [] })
        onAdopt(created.id)
        navigate(`/chat/${created.id}`, { replace: true })
      } catch (err) {
        setError(err instanceof ApiError ? err.message : 'The chat could not be created. Check the connection and send again.')
        return false
      }
    }
    return run(conversationId, 'messages', { content: text, attachments: files.map((f) => f.id) }, [
      ...messages,
      { ...blank(`local-${Date.now()}`, 'user', text), attachments: files },
    ])
  }

  const settings = useMutation({
    mutationFn: (change: object) => api(`/api/chat/conversations/${id}`, { method: 'PATCH', body: change }),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/chat/conversations/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      onChanged()
      navigate('/chat')
    },
  })

  const groups = group(messages)
  return (
    <div className="thread">
      <header className="thread-head">
        {id ? (
          <input
            className="thread-title"
            aria-label="Chat title"
            value={title}
            onChange={(e) => setTitleEdit(e.target.value)}
            onBlur={() => titleEdit !== null && settings.mutate({ title: titleEdit }, { onSuccess: onChanged })}
          />
        ) : (
          <h1 className="thread-title">New chat</h1>
        )}
        <div className="row">
          <label className="inline-label">
            Thinking
            <select
              value={thinking}
              onChange={(e) => {
                setThinking(e.target.value)
                if (id) settings.mutate({ thinking: e.target.value })
              }}
            >
              <option value="">Default{config.defaultThinking ? ` (${config.presets.find((p) => p.level === config.defaultThinking)?.label ?? config.defaultThinking})` : ''}</option>
              {config.presets.map((p) => (
                <option key={p.level} value={p.level}>
                  {p.label}
                </option>
              ))}
            </select>
          </label>
          {config.argus && (
            <label className="check">
              <input
                type="checkbox"
                checked={useArgus}
                onChange={(e) => {
                  setUseArgus(e.target.checked)
                  if (id) settings.mutate({ useArgus: e.target.checked })
                }}
              />{' '}
              Search our code (Argus)
            </label>
          )}
          {id && (
            <button type="button" className="link small" onClick={() => window.confirm('Delete this chat?') && remove.mutate()}>
              Delete
            </button>
          )}
        </div>
      </header>

      <div className="messages" aria-live="polite">
        {loaded.isPending && id && <p aria-busy="true">Loading…</p>}
        {!id && messages.length === 0 && (
          <div className="empty-chat">
            <h2>Ask {config.model ?? 'the model'} anything</h2>
            <p className="muted">
              {config.argus ? 'It can search the code you have access to in GitLab, and it says so when something exists that you cannot read.' : 'Attach text, code or PDFs to ask about them.'}
            </p>
          </div>
        )}
        {groups.map((g, i) => (
          <div key={g.user?.id ?? i}>
            {g.user && <UserTurn m={g.user} />}
            {(g.answer.length > 0 || (streaming && i === groups.length - 1)) && (
              <AssistantTurn
                parts={g.answer}
                live={streaming && i === groups.length - 1}
                notices={i === groups.length - 1 ? notices : []}
                onRegenerate={!streaming && i === groups.length - 1 && id ? () => void run(id, 'regenerate', {}, messages.slice(0, messages.length - g.answer.length)) : undefined}
              />
            )}
          </div>
        ))}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div ref={bottom} />
      </div>

      <Composer streaming={streaming} onSend={send} onStop={() => abort.current?.abort()} />
    </div>
  )
}

function Composer({ streaming, onSend, onStop }: { streaming: boolean; onSend: (text: string, files: Attachment[]) => Promise<boolean>; onStop: () => void }) {
  const [text, setText] = useState('')
  const [files, setFiles] = useState<Attachment[]>([])
  const [uploading, setUploading] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const area = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    const el = area.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 280)}px`
  }, [text])

  const submit = async (e?: FormEvent) => {
    e?.preventDefault()
    if (streaming || uploading || (!text.trim() && !files.length)) return
    const t = text.trim()
    const f = files
    setText('')
    setFiles([])
    // Not sent (the server never had it): give it back rather than lose it.
    if (!(await onSend(t, f))) {
      setText((now) => now || t)
      setFiles((now) => (now.length ? now : f))
    }
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void submit()
    }
  }
  const attach = async (list: FileList | null) => {
    setError(null)
    for (const file of Array.from(list ?? [])) {
      setUploading((n) => n + 1)
      try {
        const a = await upload(file)
        setFiles((fs) => [...fs, a])
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Upload failed.')
      } finally {
        setUploading((n) => n - 1)
      }
    }
  }

  return (
    <form className="composer" onSubmit={submit}>
      {files.length > 0 && (
        <ul className="attachments">
          {files.map((f) => (
            <li key={f.id} className="file-chip">
              {f.fileName} <span className="muted">{formatValue(f.size, 'bytes')}{f.truncated ? ', cut to fit' : ''}</span>
              <button type="button" className="link small" aria-label={`Remove ${f.fileName}`} onClick={() => setFiles((fs) => fs.filter((x) => x.id !== f.id))}>
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="composer-row">
        <label className="button secondary attach" title="Attach text, code or PDF">
          Attach
          <input type="file" multiple hidden onChange={(e) => { void attach(e.target.files); e.target.value = '' }} />
        </label>
        <textarea
          ref={area}
          rows={1}
          value={text}
          placeholder="Message"
          aria-label="Message"
          onChange={(e) => setText(e.target.value)}
          onKeyDown={onKey}
        />
        {streaming ? (
          <button type="button" className="button danger" onClick={onStop}>
            Stop
          </button>
        ) : (
          <button className="button" disabled={!!uploading || (!text.trim() && !files.length)}>
            {uploading ? 'Uploading…' : 'Send'}
          </button>
        )}
      </div>
      <p className="muted small hint">Enter sends, Shift+Enter adds a line.</p>
    </form>
  )
}
