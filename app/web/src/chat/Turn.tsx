import { useEffect, useMemo, useRef, useState } from 'react'
import { formatValue } from '../format'
import type { Message } from './api'
import { renderMarkdown } from './markdown'

export interface Notice {
  kind: string
  text: string
}

export function Markdown({ text }: { text: string }) {
  const ref = useRef<HTMLDivElement>(null)
  const html = useMemo(() => renderMarkdown(text), [text])
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const onClick = async (e: MouseEvent) => {
      const button = (e.target as HTMLElement).closest('button.copy-code')
      if (!button) return
      const code = button.closest('.code')?.querySelector('code')?.textContent ?? ''
      try {
        await navigator.clipboard.writeText(code)
        button.textContent = 'Copied'
        setTimeout(() => (button.textContent = 'Copy'), 1500)
      } catch {
        button.textContent = 'Copy failed'
      }
    }
    el.addEventListener('click', onClick)
    return () => el.removeEventListener('click', onClick)
  }, [])
  return <div ref={ref} className="markdown answer" dangerouslySetInnerHTML={{ __html: html }} />
}

export function UserTurn({ m }: { m: Message }) {
  return (
    <div className="turn user-turn" aria-label="You">
      <div className="bubble">
        {m.content && <p className="user-text">{m.content}</p>}
        {m.attachments.length > 0 && (
          <ul className="attachments">
            {m.attachments.map((a) => (
              <li key={a.id} className="file-chip">
                {a.fileName} <span className="muted">{formatValue(a.size, 'bytes')}{a.truncated ? ', cut to fit' : ''}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

function Reasoning({ text, live }: { text: string; live: boolean }) {
  return (
    <details className="reasoning" open={live}>
      <summary>{live ? 'Thinking…' : 'Thought process'}</summary>
      <div className="reasoning-text">{text}</div>
    </details>
  )
}

function ToolUse({ name, args, result }: { name: string; args: string; result?: Message }) {
  const shownArgs = useMemo(() => {
    try {
      return Object.entries(JSON.parse(args) as Record<string, unknown>).map(([k, v]) => `${k}: ${typeof v === 'string' ? v : JSON.stringify(v)}`).join(', ')
    } catch {
      return args
    }
  }, [args])
  return (
    <>
      <details className="tool">
        <summary>
          <span className="tool-name">{name}</span> <span className="muted">{shownArgs}</span>{' '}
          {!result ? <span className="muted">running…</span> : result.status === 'failed' ? <span className="warn-text">failed</span> : null}
        </summary>
        {result && <pre className="code-block">{result.content}</pre>}
      </details>
      {result?.noAccess && (
        <div className="notice warn no-access" role="note">
          <strong>You do not have access to some of this code.</strong>
          <pre className="plain-pre">{result.content.split('\n').filter((l) => l.startsWith('- ')).join('\n')}</pre>
          Ask a maintainer listed above to add you in GitLab with at least Reporter access. Argus picks the change up within 10 minutes.
        </div>
      )}
    </>
  )
}

/** Everything after one question: thinking, tool use and the answer, possibly over several rounds. */
export function AssistantTurn({ parts, live, notices, onRegenerate }: { parts: Message[]; live: boolean; notices: Notice[]; onRegenerate?: () => void }) {
  const results = new Map(parts.filter((p) => p.role === 'tool').map((p) => [p.toolCallId, p]))
  const assistants = parts.filter((p) => p.role === 'assistant')
  const last = assistants[assistants.length - 1]
  const answer = assistants.map((a) => a.content).filter(Boolean).join('\n\n')
  const [copied, setCopied] = useState(false)
  return (
    <div className="turn assistant-turn" aria-label="Answer" aria-busy={live}>
      {notices.map((n, i) => (
        <p key={i} className="muted notice-line" role="status">
          {n.text}
        </p>
      ))}
      {assistants.map((a, i) => (
        <div key={a.id}>
          {a.reasoning && <Reasoning text={a.reasoning} live={live && i === assistants.length - 1 && !a.content} />}
          {a.content && <Markdown text={a.content} />}
          {a.toolCalls?.map((t) => <ToolUse key={t.id} name={t.function.name} args={t.function.arguments} result={results.get(t.id)} />)}
          {a.error && (
            <p className="error" role="alert">
              {a.error}
            </p>
          )}
        </div>
      ))}
      {live && !answer && !last?.reasoning && <div className="typing" aria-label="Answering" />}
      {!live && last && (
        <div className="turn-actions">
          {last.status === 'stopped' && <span className="muted">Stopped.</span>}
          {answer && (
            <button type="button" className="link small" onClick={async () => { await navigator.clipboard.writeText(answer); setCopied(true); setTimeout(() => setCopied(false), 1500) }}>
              {copied ? 'Copied' : 'Copy'}
            </button>
          )}
          {onRegenerate && (
            <button type="button" className="link small" onClick={onRegenerate}>
              Regenerate
            </button>
          )}
          {last.completionTokens !== null && (
            <span className="muted small" title="Input tokens (from cache) → output tokens">
              {formatValue(last.promptTokens ?? 0)} in{last.cachedTokens ? ` (${formatValue(last.cachedTokens)} cached)` : ''} · {formatValue(last.completionTokens)} out
            </span>
          )}
        </div>
      )}
    </div>
  )
}
