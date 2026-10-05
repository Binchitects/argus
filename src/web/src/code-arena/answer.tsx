import { Check, Copy, FileDiff as FileDiffIcon, ShieldQuestion, Square } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue, money } from '@/lib/format'
import { cn } from '@/lib/utils'
import { answerUsage, seconds, toolTitle } from '@/pages/chat/format'
import type { Notice, ToolRunning } from '@/pages/chat/live'
import { Markdown } from '@/pages/chat/markdown'
import { NoticeLine, Thinking, ToolCard } from '@/pages/chat/parts'
import type { ChatConfig, Message } from '@/pages/chat/types'
import type { FileDiff } from './api'

/** An edit: the file's changed lines with three around them, numbered when the file was read whole (a live edit). */
export function DiffView({ diff }: { diff: FileDiff }) {
  const numbered = diff.lines.some(([, a, b]) => a > 0 || b > 0)
  return (
    <figure className="my-2 overflow-hidden rounded-lg border bg-card text-xs" aria-label={`Changes to ${diff.path}`}>
      <figcaption className="flex min-w-0 items-center gap-2 border-b px-3 py-1.5">
        <FileDiffIcon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        <span className="min-w-0 truncate font-mono font-medium">{diff.path}</span>
        {diff.created && <span className="text-muted-foreground">new file</span>}
        <span className="ml-auto shrink-0 font-mono tabular-nums">
          <span className="text-success">+{diff.added}</span> <span className="text-destructive-ink">−{diff.removed}</span>
        </span>
      </figcaption>
      <div className="max-h-96 overflow-auto">
        <table className="w-full border-collapse font-mono leading-5">
          <tbody>
            {diff.lines.map(([op, a, b, text], i) =>
              op === '⋮' ? (
                <tr key={i} className="bg-muted/50 text-muted-foreground">
                  <td colSpan={numbered ? 4 : 2} className="px-3 select-none">
                    ⋮
                  </td>
                </tr>
              ) : (
                <tr key={i} className={cn(op === '-' && 'bg-destructive/10', op === '+' && 'bg-success/10')}>
                  {numbered && <td className="w-px px-2 text-right text-muted-foreground select-none tabular-nums">{a || ''}</td>}
                  {numbered && <td className="w-px px-2 text-right text-muted-foreground select-none tabular-nums">{b || ''}</td>}
                  <td className={cn('w-px pl-2 select-none', op === '-' ? 'text-destructive-ink' : op === '+' ? 'text-success' : 'text-muted-foreground')}>
                    <span aria-hidden="true">{op === ' ' ? '' : op === '-' ? '−' : '+'}</span>
                    <span className="sr-only">{op === '-' ? 'Removed:' : op === '+' ? 'Added:' : ''}</span>
                  </td>
                  <td className="pr-3 pl-2 whitespace-pre">{text}</td>
                </tr>
              ),
            )}
          </tbody>
        </table>
      </div>
      {diff.more > 0 && <p className="border-t px-3 py-1.5 text-muted-foreground">… {diff.more} more changed {diff.more === 1 ? 'line' : 'lines'}</p>}
    </figure>
  )
}

/** A call waiting for the person, as Arena asks before a tool runs: Allow, Always for this session, or Deny; for a command, what Laya made of it. */
export function Approval({ name, always, risk, onDecide }: { name: string; always?: string; risk?: string; onDecide: (answer: 'allow' | 'always' | 'deny') => void }) {
  return (
    <div role="alert" className="mt-2 flex flex-wrap items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
      <ShieldQuestion className="size-4 shrink-0 text-warning-ink" aria-hidden="true" />
      <span className="min-w-0 flex-1">
        Allow <strong>{toolTitle(name)}</strong> to run with these arguments?
        {risk && <span className="block text-xs">{risk}</span>}
        {always && <span className="block text-xs text-muted-foreground">Always for this session: no more questions {always}.</span>}
      </span>
      <Button size="sm" variant="outline" className="h-7" onClick={() => onDecide('deny')}>
        Deny
      </Button>
      <Button size="sm" variant="outline" className="h-7" onClick={() => onDecide('always')}>
        Always for this session
      </Button>
      <Button size="sm" className="h-7" onClick={() => onDecide('allow')}>
        Allow
      </Button>
    </div>
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

/**
 * Everything that answered a message: thinking, text, tool cards with their
 * diffs and questions, and a line with the model, time and tokens (the share
 * read from the cache with them), as Arena's chat shows an answer.
 */
export function CodeAnswer({
  answer,
  live,
  thinkingSince,
  notices,
  config,
  waiting,
  always,
  risks,
  calls,
  diffs,
  onDecide,
}: {
  answer: Message[]
  live: boolean
  thinkingSince: number | null
  notices: Notice[]
  config: ChatConfig
  /** Calls waiting for the person, and what "always" covers for each. */
  waiting: string[]
  always: Record<string, string>
  /** Laya's probabilities for a command waiting, by call. */
  risks?: Record<string, string>
  calls?: Record<string, ToolRunning>
  diffs: Record<string, FileDiff>
  onDecide: (callId: string, answer: 'allow' | 'always' | 'deny') => void
}) {
  const results = new Map(answer.filter((m) => m.role === 'tool').map((m) => [m.toolCallId, m]))
  const assistants = answer.filter((m) => m.role === 'assistant')
  const text = assistants.map((a) => a.content).filter(Boolean).join('\n\n')
  const last = assistants.at(-1)
  const took = answer.reduce((n, m) => n + (m.durationMs ?? 0), 0)
  const usage = answerUsage(answer, config)
  const cached = usage.prompt > 0 ? Math.round((usage.cached / usage.prompt) * 100) : null
  const pending = live && assistants.every((a) => !a.content && !a.reasoning && !a.toolCalls?.length)
  return (
    <section className={cn('min-w-0 animate-enter', live && 'pb-2')} aria-label="Answer" aria-busy={live}>
      {notices.map((n, i) => (
        <NoticeLine key={i} text={n.text} />
      ))}
      {pending && (
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
          <div key={a.id} data-message={a.id} className="scroll-mt-4 rounded-lg">
            {a.reasoning && <Thinking text={a.reasoning} live={live && isLast && !a.content && !a.toolCalls?.length} ms={a.thinkingMs} since={isLast ? thinkingSince : null} />}
            {a.content && <Markdown text={a.content} live={live && isLast} />}
            {live && isLast && a.content && <span className="ml-0.5 inline-block h-4 w-1.5 animate-pulse rounded-sm bg-primary align-middle" aria-hidden="true" />}
            {a.toolCalls?.map((t) => {
              const result = results.get(t.id)
              const asking = live && waiting.includes(t.id)
              const diff = result ? diffs[result.id] : undefined
              return (
                <div key={t.id}>
                  <ToolCard call={t} result={result} live={live} waiting={asking} progress={calls?.[t.id]} />
                  {diff && <DiffView diff={diff} />}
                  {asking && <Approval name={t.function.name} always={always[t.id]} risk={risks?.[t.id]} onDecide={(answer) => onDecide(t.id, answer)} />}
                </div>
              )
            })}
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
      {!live && last && (
        <div className="mt-1 flex flex-wrap items-center gap-x-1 text-xs text-muted-foreground">
          {text && <CopyButton text={text} label="Copy answer" />}
          <span className="ml-1 flex flex-wrap items-center gap-x-2 tabular-nums">
            {last.model && <span>{last.model}</span>}
            {took > 0 && <span>· {seconds(took)}</span>}
            {usage.prompt + usage.completion > 0 && (
              <span title={`${usage.prompt.toLocaleString()} in (${usage.cached.toLocaleString()} from cache), ${usage.completion.toLocaleString()} out`}>
                · {formatValue(usage.prompt)} in{cached !== null && ` (${cached}% cached)`} · {formatValue(usage.completion)} out
              </span>
            )}
            {usage.cost !== null && <span>· {money(usage.cost)}</span>}
          </span>
        </div>
      )}
    </section>
  )
}
