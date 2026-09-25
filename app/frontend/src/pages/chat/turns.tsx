import { Check, ChevronLeft, ChevronRight, Copy, FileText, GitFork, Pencil, RefreshCw, Square } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Textarea } from '@/components/ui/input'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { attachmentUrl } from './api'
import { ImageViewer } from './image-viewer'
import type { Notice } from './live'
import { Markdown } from './markdown'
import { answerCost, seconds } from './format'
import { NoticeLine, Thinking, ToolCard } from './parts'
import type { ChatConfig, Message } from './types'

/** "2 / 3" with arrows: the alternatives to a question or an answer. */
function Branches({ siblings, current, onSwitch, label }: { siblings: Message[]; current: Message; onSwitch: (id: string) => void; label: string }) {
  if (siblings.length < 2) return null
  const i = siblings.findIndex((s) => s.id === current.id)
  return (
    <nav aria-label={`${label} versions`} className="flex items-center text-xs text-muted-foreground tabular-nums">
      <Button variant="ghost" size="icon-sm" className="size-7" disabled={i <= 0} onClick={() => onSwitch(siblings[i - 1]!.id)} aria-label={`Previous ${label.toLowerCase()} version`}>
        <ChevronLeft />
      </Button>
      <span aria-live="polite">
        {i + 1} / {siblings.length}
      </span>
      <Button variant="ghost" size="icon-sm" className="size-7" disabled={i >= siblings.length - 1} onClick={() => onSwitch(siblings[i + 1]!.id)} aria-label={`Next ${label.toLowerCase()} version`}>
        <ChevronRight />
      </Button>
    </nav>
  )
}

function CopyButton({ text, label }: { text: string; label: string }) {
  const [done, setDone] = useState(false)
  return (
    <Tooltip content={done ? 'Copied' : label}>
      <Button
        variant="ghost"
        size="icon-sm"
        className="size-7"
        aria-label={done ? 'Copied' : label}
        onClick={async () => {
          await navigator.clipboard.writeText(text).catch(() => undefined)
          setDone(true)
          setTimeout(() => setDone(false), 1500)
        }}
      >
        {done ? <Check /> : <Copy />}
      </Button>
    </Tooltip>
  )
}

export function QuestionTurn({ m, siblings, busy, onSwitch, onEdit }: { m: Message; siblings: Message[]; busy: boolean; onSwitch: (id: string) => void; onEdit: (m: Message, text: string) => void }) {
  const [editing, setEditing] = useState<string | null>(null)
  const [viewing, setViewing] = useState<number | null>(null)
  const images = m.attachments.filter((a) => a.kind === 'image')
  return (
    <section className="group/q flex animate-enter scroll-mt-4 flex-col items-end gap-1 outline-none" aria-label="You" data-question={m.id} tabIndex={-1}>
      {m.attachments.length > 0 && (
        <ul className="flex max-w-[85%] flex-wrap justify-end gap-2" aria-label="Attachments">
          {m.attachments.map((a) =>
            a.kind === 'image' ? (
              <li key={a.id}>
                <button
                  type="button"
                  onClick={() => setViewing(images.indexOf(a))}
                  className="block cursor-zoom-in overflow-hidden rounded-lg border outline-none focus-visible:ring-[3px] focus-visible:ring-ring"
                  aria-label={`View ${a.fileName}`}
                >
                  <img src={attachmentUrl(a.id)} alt={a.fileName} className="h-24 max-w-48 object-cover" loading="lazy" />
                </button>
              </li>
            ) : (
              <li key={a.id} className="flex items-center gap-2 rounded-lg border bg-card px-3 py-2 text-sm">
                <FileText className="size-4 text-muted-foreground" aria-hidden="true" />
                <span className="max-w-48 truncate">{a.fileName}</span>
                <span className="text-xs text-muted-foreground">{formatValue(a.size, 'bytes')}</span>
              </li>
            ),
          )}
        </ul>
      )}
      {images.length > 0 && <ImageViewer images={images} index={viewing} onIndex={setViewing} />}
      {editing === null ? (
        m.content && <div className="max-w-[85%] rounded-2xl rounded-br-md bg-secondary px-4 py-2.5 text-[0.9375rem] leading-relaxed whitespace-pre-wrap text-secondary-foreground">{m.content}</div>
      ) : (
        <form
          className="grid w-full max-w-[85%] gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            if (editing.trim()) onEdit(m, editing.trim())
            setEditing(null)
          }}
        >
          <Textarea value={editing} onChange={(e) => setEditing(e.target.value)} aria-label="Edit your question" autoFocus className="min-h-24" />
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => setEditing(null)}>
              Cancel
            </Button>
            <Button type="submit" size="sm" disabled={busy || !editing.trim()}>
              Send
            </Button>
          </div>
        </form>
      )}
      {editing === null && (
        <div className="flex items-center opacity-100 transition-opacity focus-within:opacity-100 sm:opacity-0 sm:group-hover/q:opacity-100 [@media(hover:none)]:opacity-100">
          <Branches siblings={siblings} current={m} onSwitch={onSwitch} label="Question" />
          <CopyButton text={m.content} label="Copy question" />
          <Tooltip content="Edit: a new version, the old one stays">
            <Button variant="ghost" size="icon-sm" className="size-7" disabled={busy} onClick={() => setEditing(m.content)} aria-label="Edit question">
              <Pencil />
            </Button>
          </Tooltip>
        </div>
      )}
    </section>
  )
}

export function AnswerTurn({
  answer,
  siblings,
  live,
  thinkingSince,
  notices,
  config,
  question,
  onSwitch,
  onRegenerate,
  onOpenFile,
  onFork,
  approvals,
  onDecide,
  busy,
}: {
  answer: Message[]
  siblings: Message[]
  live: boolean
  thinkingSince: number | null
  notices: Notice[]
  config: ChatConfig
  question?: Message
  onSwitch: (id: string) => void
  onRegenerate?: (question: Message, overrides?: { model?: string; thinking?: string }) => void
  onOpenFile: (name: string) => void
  /** Fork into a new chat that ends with this answer. */
  onFork?: (messageId: string) => void
  /** Tool calls waiting for the person to allow them, and how to answer. */
  approvals?: string[]
  onDecide?: (callId: string, allow: boolean) => void
  busy: boolean
}) {
  const results = new Map(answer.filter((m) => m.role === 'tool').map((m) => [m.toolCallId, m]))
  const assistants = answer.filter((m) => m.role === 'assistant')
  const first = assistants[0]
  const last = assistants.at(-1)
  const text = assistants.map((a) => a.content).filter(Boolean).join('\n\n')
  const tokens = assistants.reduce((a, m) => ({ in: a.in + (m.promptTokens ?? 0), cached: a.cached + (m.cachedTokens ?? 0), out: a.out + (m.completionTokens ?? 0) }), { in: 0, cached: 0, out: 0 })
  const took = assistants.reduce((a, m) => a + (m.durationMs ?? 0), 0) + answer.filter((m) => m.role === 'tool').reduce((a, m) => a + (m.durationMs ?? 0), 0)
  const cost = answerCost(assistants, config)
  const waiting = live && assistants.every((a) => !a.content && !a.reasoning && !a.toolCalls?.length)

  let footer: ReactNode = null
  if (!live && first) {
    footer = (
      <div className="mt-1 flex flex-wrap items-center gap-x-1 text-xs text-muted-foreground">
        <Branches siblings={siblings} current={first} onSwitch={onSwitch} label="Answer" />
        <CopyButton text={text} label="Copy answer" />
        {onRegenerate && question && (
          <DropdownMenu>
            <Tooltip content="Answer again">
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon-sm" className="size-7" disabled={busy} aria-label="Answer again">
                  <RefreshCw />
                </Button>
              </DropdownMenuTrigger>
            </Tooltip>
            <DropdownMenuContent align="start">
              <DropdownMenuItem onSelect={() => onRegenerate(question)}>
                <RefreshCw /> Answer again
              </DropdownMenuItem>
              {config.models.filter((mo) => mo.loaded).length > 1 && (
                <>
                  <DropdownMenuSeparator />
                  <DropdownMenuLabel>With another model</DropdownMenuLabel>
                  {config.models.filter((mo) => mo.loaded).map((mo) => (
                    <DropdownMenuItem key={mo.name} onSelect={() => onRegenerate(question, { model: mo.name })}>
                      {mo.name}
                    </DropdownMenuItem>
                  ))}
                </>
              )}
              {config.presets.length > 0 && (
                <>
                  <DropdownMenuSeparator />
                  <DropdownMenuLabel>With thinking</DropdownMenuLabel>
                  {config.presets.map((p) => (
                    <DropdownMenuItem key={p.level} onSelect={() => onRegenerate(question, { thinking: p.level })}>
                      {p.label}
                    </DropdownMenuItem>
                  ))}
                </>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
        {onFork && last && !last.toolCalls?.length && !last.id.startsWith('local-') && (
          <Tooltip content="Fork into a new chat from here">
            <Button variant="ghost" size="icon-sm" className="size-7" disabled={busy} onClick={() => onFork(last.id)} aria-label="Fork from here">
              <GitFork />
            </Button>
          </Tooltip>
        )}
        <span className="ml-1 flex flex-wrap items-center gap-x-2 tabular-nums">
          {last?.model && <span>{last.model}</span>}
          {took > 0 && <span>· {seconds(took)}</span>}
          {tokens.in + tokens.out > 0 && (
            <span title={`${tokens.in.toLocaleString()} in (${tokens.cached.toLocaleString()} from cache), ${tokens.out.toLocaleString()} out`}>
              · {formatValue(tokens.in)} in · {formatValue(tokens.out)} out
            </span>
          )}
          {cost !== null && <span>· ${cost < 0.01 ? Number(cost.toPrecision(2)) : cost.toFixed(2)}</span>}
        </span>
      </div>
    )
  }

  return (
    <section className={cn('min-w-0 animate-enter', live && 'pb-2')} aria-label="Answer" aria-busy={live}>
      {notices.map((n, i) => (
        <NoticeLine key={i} text={n.text} />
      ))}
      {waiting && (
        <output className="flex items-center gap-2 text-sm text-muted-foreground">
          <span className="flex gap-1" aria-hidden="true">
            <span className="size-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.3s]" />
            <span className="size-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.15s]" />
            <span className="size-1.5 animate-bounce rounded-full bg-current" />
          </span>
          Waiting for the model…
        </output>
      )}
      {assistants.map((a, i) => {
        const isLast = i === assistants.length - 1
        return (
          <div key={a.id}>
            {a.reasoning && <Thinking text={a.reasoning} live={live && isLast && !a.content && !a.toolCalls?.length} ms={a.thinkingMs} since={isLast ? thinkingSince : null} />}
            {a.content && <Markdown text={a.content} onOpenFile={onOpenFile} />}
            {live && isLast && a.content && <span className="ml-0.5 inline-block h-4 w-1.5 animate-pulse rounded-sm bg-primary align-middle" aria-hidden="true" />}
            {a.toolCalls?.map((t) => (
              <ToolCard key={t.id} call={t} result={results.get(t.id)} live={live} waiting={approvals?.includes(t.id)} onDecide={onDecide ? (allow) => onDecide(t.id, allow) : undefined} onOpenFile={onOpenFile} />
            ))}
            {a.error && (
              <Alert variant="destructive" className="my-2">
                {a.error}
              </Alert>
            )}
            {a.status === 'stopped' && (
              <p className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
                <Square className="size-3" aria-hidden="true" /> Stopped.
              </p>
            )}
          </div>
        )
      })}
      {footer}
    </section>
  )
}
