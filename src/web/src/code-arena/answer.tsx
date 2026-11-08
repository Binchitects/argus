import { Check, ChevronRight, CircleSlash, CircleX, Copy, FileDiff as FileDiffIcon, FileSymlink, History, Network, ShieldQuestion, Square, TextQuote, Wrench } from 'lucide-react'
import { memo, useEffect, useMemo, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { DropdownMenuItem, DropdownMenuSeparator } from '@/components/ui/dropdown-menu'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue, money } from '@/lib/format'
import { cn } from '@/lib/utils'
import { answerUsage, seconds, toolTitle } from '@/pages/chat/format'
import { refLabel, useFileRefs, type FileRef } from '@/pages/chat/file-refs'
import { ImageViewer } from '@/pages/chat/image-viewer'
import { waitsForModel, type Notice, type ToolRunning } from '@/pages/chat/live'
import { Markdown } from '@/pages/chat/markdown'
import { MediaPlayer } from '@/pages/chat/media'
import { NoticeLine, Thinking, ToolCard } from '@/pages/chat/parts'
import type { Quote } from '@/pages/chat/quote'
import { QuotePartDialog } from '@/pages/chat/quote-parts'
import { AnswerAgainMenu, type AgainChange } from '@/pages/chat/turns'
import type { AgentWork, ChatConfig, Message } from '@/pages/chat/types'
import type { FileDiff } from './api'
import { FileIcon } from './file-icon'
import { nameOf, parentOf, rawFileUrl } from './ide-api'
import { mediaNamed } from './media'
import { tokens } from './tasks-api'

/** A sub-agent's work, from what the chat heard of it (live) or what the session kept with its result. */
function workOf(result: Message | undefined, live: AgentWork | undefined): AgentWork | undefined {
  return live ?? result?.details?.agents?.[0]
}

/**
 * A sub-agent the agent started (a task call), as one line: what it is for, where it stands, its time and tokens, and the
 * start of its report once done. Its steps are its own: Open shows them in the Tasks panel beside the chat.
 */
export function SubAgentLine({ callId, args, result, live, work, onOpen }: {
  callId: string
  args: string
  result?: Message
  live: boolean
  work?: AgentWork
  onOpen?: (callId: string) => void
}) {
  const title = useMemo(() => {
    try {
      const a = JSON.parse(args) as { description?: string }
      return a.description?.trim() || 'Sub-agent'
    } catch {
      return 'Sub-agent'
    }
  }, [args])
  // Stopped: by the person (from the Tasks panel, or with the turn), as code-arena says it.
  const stopped = (work?.status as string) === 'stopped' || (work?.error ?? '').startsWith('Stopped') || !!result?.content.startsWith('The person stopped')
  const status: 'running' | 'done' | 'failed' | 'stopped' = !result
    ? live ? 'running' : 'stopped'
    : stopped ? 'stopped' : work?.status === 'failed' || result.error || result.status === 'failed' ? 'failed' : 'done'
  // While it works: its time counted here, from when the line first showed.
  const [since] = useState(() => Date.now())
  const [now, setNow] = useState(since)
  useEffect(() => {
    if (status !== 'running') return
    const tick = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(tick)
  }, [status])
  const spent = work?.usage ? work.usage.prompt + work.usage.completion : 0
  const facts = [
    status === 'running' ? seconds(now - since) : work?.ms != null && seconds(work.ms),
    spent > 0 && `${tokens(spent)} tokens`,
    work?.model,
  ].filter(Boolean).join(' · ')
  const words = { running: 'Working', done: 'Done', failed: 'Failed', stopped: 'Stopped' }[status]
  const report = status === 'done' ? result?.content.split('\n').find((l) => l.trim()) : undefined
  return (
    <div className="my-1.5 min-w-0">
      <button
        type="button"
        onClick={() => onOpen?.(callId)}
        disabled={!onOpen}
        aria-label={`Sub-agent ${title}: ${words}. Open it in Tasks`}
        className={cn(
          'flex w-full min-w-0 items-center gap-2 rounded-lg border bg-card px-3 py-1.5 text-left text-sm transition-colors outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring disabled:cursor-default',
          status === 'running' && 'border-primary/40',
        )}
      >
        {status === 'running' ? (
          <Spinner className="shrink-0 text-primary-ink" />
        ) : status === 'done' ? (
          <Check className="size-4 shrink-0 text-success" aria-hidden="true" />
        ) : status === 'failed' ? (
          <CircleX className="size-4 shrink-0 text-destructive-ink" aria-hidden="true" />
        ) : (
          <CircleSlash className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        )}
        <Network className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        <bdi className="min-w-0 shrink truncate font-medium">{title}</bdi>
        <span className={cn('min-w-0 flex-1 truncate text-xs text-muted-foreground tabular-nums', status === 'running' && 'text-shimmer')}>
          {[status === 'running' ? 'Working…' : report ?? words, facts].filter(Boolean).join(' · ')}
        </span>
        {onOpen && <ChevronRight className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />}
      </button>
    </div>
  )
}

type ToolCall = NonNullable<Message['toolCalls']>[number]

/** A message's tool calls in runs: the sub-agents started one after another, and the other calls between them. */
export function runsOf(calls: ToolCall[]): { kind: 'task' | 'tools'; calls: ToolCall[] }[] {
  const runs: { kind: 'task' | 'tools'; calls: ToolCall[] }[] = []
  for (const c of calls) {
    const kind = c.function.name === 'task' ? 'task' : 'tools'
    const last = runs[runs.length - 1]
    if (last && last.kind === kind) last.calls.push(c)
    else runs.push({ kind, calls: [c] })
  }
  return runs
}

/** "Used 5 tools · read_file ×3, grep, run_shell": the tools a run called, the most called first. */
export function toolsSummary(names: string[]): string {
  const counts = new Map<string, number>()
  for (const n of names) counts.set(n, (counts.get(n) ?? 0) + 1)
  const parts = [...counts].sort((a, b) => b[1] - a[1]).map(([n, c]) => (c > 1 ? `${n} ×${c}` : n))
  return `Used ${names.length} tools · ${parts.join(', ')}`
}

/** "3 sub-agents · 2 working", or "3 sub-agents · done" once each has reported. */
export function subAgentsSummary(done: boolean[], n: number): string {
  const left = done.filter((d) => !d).length
  return `${n} sub-agent${n === 1 ? '' : 's'} · ${left === 0 ? 'done' : `${left} working`}`
}

/**
 * Several tool calls (or sub-agents) as one line that folds, as Claude's apps show them: open while they run, folded to
 * their summary once done (the person's choice kept); forced open while one waits for the person.
 */
function ToolRun({ icon, summary, working, forceOpen, children }: { icon: 'tools' | 'agents'; summary: string; working: boolean; forceOpen: boolean; children: React.ReactNode }) {
  const [chosen, setChosen] = useState<boolean | null>(null)
  const open = forceOpen || (chosen ?? working)
  const Icon = icon === 'agents' ? Network : Wrench
  return (
    <div className="my-1.5 min-w-0">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setChosen(!open)}
        className="flex w-full min-w-0 items-center gap-2 rounded-md px-1.5 py-1 text-left text-xs text-muted-foreground outline-none hover:bg-accent/50 hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring"
      >
        <ChevronRight className={cn('size-3.5 shrink-0 transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
        {working ? <Spinner className="size-3.5 shrink-0 text-primary-ink" /> : <Icon className="size-3.5 shrink-0" aria-hidden="true" />}
        <span className={cn('min-w-0 truncate', working && 'text-shimmer')}>{summary}</span>
      </button>
      {open && <div className="ml-3 border-l pl-3">{children}</div>}
    </div>
  )
}

/** The first line an edit changed, in the file as it is now; null: not known (an edit of a session read back). */
const firstChanged = (diff: FileDiff) => diff.lines.find(([op, , b]) => op === '+' && b > 0)?.[2] ?? diff.lines.find(([, , b]) => b > 0)?.[2] ?? null

/**
 * An edit: the file's changed lines with three around them, numbered when the file was read whole (a live edit). Its
 * chevron folds them, as VS Code's chat does: open while the turn runs, its caption alone once it is over (unless the
 * person chose). In the IDE, its path opens the file at the first line changed, and each line's number opens it there.
 */
export function DiffView({ diff, live = false }: { diff: FileDiff; live?: boolean }) {
  const numbered = diff.lines.some(([, a, b]) => a > 0 || b > 0)
  const refs = useFileRefs()
  const path = refs?.known(diff.path) ?? null
  const first = firstChanged(diff)
  const [chosen, setOpen] = useState<boolean | null>(null)
  const open = chosen ?? live
  return (
    <figure className="my-2 overflow-hidden rounded-lg border bg-card text-xs" aria-label={`Changes to ${diff.path}`}>
      <figcaption className={cn('flex min-w-0 items-center gap-2 py-1.5 pr-3 pl-1.5', open && 'border-b')}>
        <button
          type="button"
          aria-expanded={open}
          aria-label={`Changed lines of ${diff.path}`}
          onClick={() => setOpen(!open)}
          className="grid size-5 shrink-0 place-items-center rounded-sm text-muted-foreground hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
        >
          <ChevronRight className={cn('size-3.5 transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
        </button>
        <FileDiffIcon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        {refs && path ? (
          <button
            type="button"
            className="min-w-0 truncate rounded-sm font-mono font-medium hover:underline focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
            onClick={() => refs.open({ path, ...(first && { line: first }) })}
            title={`Open ${path}${first ? `:${first}` : ''} in the editor`}
          >
            {diff.path}
          </button>
        ) : (
          <span className="min-w-0 truncate font-mono font-medium">{diff.path}</span>
        )}
        {diff.created && <span className="text-muted-foreground">new file</span>}
        <span className="ml-auto shrink-0 font-mono tabular-nums">
          <span className="text-success">+{diff.added}</span> <span className="text-destructive-ink">−{diff.removed}</span>
        </span>
      </figcaption>
      {open && <DiffLines diff={diff} numbered={numbered} path={path} />}
    </figure>
  )
}

/** An edit's lines, and how many more it changed past them. */
function DiffLines({ diff, numbered, path }: { diff: FileDiff; numbered: boolean; path: string | null }) {
  const refs = useFileRefs()
  return (
    <>
      {/* Relative: the lines' screen-reader words (absolute) stay in this box, not laid out past its end in the thread. */}
      <div className="relative max-h-96 overflow-auto">
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
                  {numbered && (
                    <td className="w-px px-2 text-right text-muted-foreground select-none tabular-nums">
                      {refs && path && b > 0 ? (
                        <button type="button" className="tabular-nums hover:text-foreground hover:underline" onClick={() => refs.open({ path, line: b })} aria-label={`Open ${path} at line ${b}`}>
                          {b}
                        </button>
                      ) : (
                        b || ''
                      )}
                    </td>
                  )}
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
    </>
  )
}

/** A file a turn changed: the lines its edits added and removed, and where the first of them changed it. */
interface TurnFile {
  path: string
  added: number
  removed: number
  line: number | null
}

/** The files a turn's edits changed, each once, in the order the turn first changed them. */
function turnFiles(answer: Message[], diffs: Record<string, FileDiff>): TurnFile[] {
  const files = new Map<string, TurnFile>()
  for (const m of answer) {
    const d = m.role === 'tool' ? diffs[m.id] : undefined
    if (!d) continue
    const f = files.get(d.path)
    if (f) {
      f.added += d.added
      f.removed += d.removed
    } else files.set(d.path, { path: d.path, added: d.added, removed: d.removed, line: firstChanged(d) })
  }
  return [...files.values()]
}

/**
 * Under a finished answer, as VS Code's chat has it: the files the turn changed, folding with its chevron. In the IDE,
 * one opens as the agent's change to review (before the agent and now), or the file once the change is kept.
 */
function TurnFiles({ files }: { files: TurnFile[] }) {
  const refs = useFileRefs()
  const [open, setOpen] = useState(true)
  const lines = (f: { added: number; removed: number }) => (
    <span className="ml-auto shrink-0 font-mono tabular-nums">
      <span className="text-success">+{f.added}</span> <span className="text-destructive-ink">−{f.removed}</span>
    </span>
  )
  return (
    <div className="mt-2 overflow-hidden rounded-lg border bg-card text-xs">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="flex w-full min-w-0 items-center gap-1.5 py-1.5 pr-3 pl-1.5 text-left font-medium outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:ring-inset"
      >
        <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
        <span>
          {files.length} {files.length === 1 ? 'file' : 'files'} changed
        </span>
        {lines({ added: files.reduce((n, f) => n + f.added, 0), removed: files.reduce((n, f) => n + f.removed, 0) })}
      </button>
      {open && (
        <ul aria-label="Files changed" className="border-t py-1">
          {files.map((f) => {
            const row = (
              <>
                <FileIcon path={f.path} className="size-3.5" />
                <span className="truncate">{nameOf(f.path)}</span>
                <span className="min-w-0 truncate text-muted-foreground">{parentOf(f.path)}</span>
                {lines(f)}
              </>
            )
            return (
              <li key={f.path}>
                {refs?.compare ? (
                  <button
                    type="button"
                    onClick={() => refs.compare?.({ path: f.path, ...(f.line && { line: f.line }) })}
                    title={`${f.path}: the agent's change to it`}
                    className="flex w-full min-w-0 items-center gap-1.5 py-0.5 pr-3 pl-6 text-left outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
                  >
                    {row}
                  </button>
                ) : (
                  <span className="flex min-w-0 items-center gap-1.5 py-0.5 pr-3 pl-6">{row}</span>
                )}
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}

/**
 * A call waiting for the person, as Arena asks before a tool runs: Allow, Always for this session (unless the call asks every
 * time, as stop_command does), or Deny; for a command, what Laya made of it.
 */
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
      {always && (
        <Button size="sm" variant="outline" className="h-7" onClick={() => onDecide('always')}>
          Always for this session
        </Button>
      )}
      <Button size="sm" className="h-7" onClick={() => onDecide('allow')}>
        Allow
      </Button>
    </div>
  )
}

/**
 * Where a tool's call on a file of the folder opens: read_file at the line it read from, an edit at the lines it changed
 * (its result says them: "Edited src/app.ts (lines 12-14).", a diff of a session read back has no numbers).
 */
function placeOf(name: string, args: string, diff: FileDiff | undefined, said: string): Omit<FileRef, 'path'> & { path: string | null } {
  let a: { path?: unknown; offset?: unknown } = {}
  try {
    a = JSON.parse(args) as typeof a
  } catch {
    // Arguments still being written.
  }
  const path = typeof a.path === 'string' && ['read_file', 'edit_file', 'write_file'].includes(name) ? a.path : null
  const lines = /\(lines? (\d+)(?:-(\d+))?\)/.exec(said)
  const line =
    name === 'read_file' ? (typeof a.offset === 'number' && a.offset > 1 ? a.offset : undefined) : lines ? Number(lines[1]) : diff ? (firstChanged(diff) ?? undefined) : undefined
  const end = name !== 'read_file' && lines?.[2] ? Number(lines[2]) : undefined
  return { path, ...(line && { line }), ...(end && { end }) }
}

/** A chip under a tool's card in the IDE: opens the file it read or changed, there. */
function OpenFile({ name, args, diff, said }: { name: string; args: string; diff: FileDiff | undefined; said: string }) {
  const refs = useFileRefs()
  const place = placeOf(name, args, diff, said)
  const path = refs && place.path ? refs.known(place.path) : null
  if (!refs || !path) return null
  const ref = { ...place, path }
  return (
    <button
      type="button"
      onClick={() => refs.open(ref)}
      className="mt-1 mb-1 inline-flex max-w-full items-center gap-1 rounded-md border px-2 py-0.5 font-mono text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
      aria-label={`Open ${refLabel(ref)} in the editor`}
    >
      <FileSymlink className="size-3.5 shrink-0" aria-hidden="true" />
      <span className="truncate">{refLabel(ref)}</span>
    </button>
  )
}

/**
 * The pictures, sounds and videos a tool made in the folder (Arena's tools keep them under .code-arena/media/ and say
 * where): shown and played under its card, as Arena's chat shows the media its tools make.
 */
function MadeMedia({ said }: { said: string }) {
  const found = useMemo(() => mediaNamed(said), [said])
  const [viewing, setViewing] = useState<number | null>(null)
  const pictures = found.filter((m) => m.kind === 'image')
  const played = found.filter((m) => m.kind !== 'image')
  return (
    <>
      {pictures.length > 0 && (
        <ul className="mt-2 flex flex-wrap gap-2" aria-label="Pictures">
          {pictures.map((p, i) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => setViewing(i)}
                className="block cursor-zoom-in overflow-hidden rounded-xl border outline-none focus-visible:ring-[3px] focus-visible:ring-ring"
                aria-label={`View ${p.fileName}`}
                title={p.id}
              >
                <img src={rawFileUrl(p.id)} alt={p.fileName} className="max-h-96 w-auto max-w-full object-contain" loading="lazy" />
              </button>
            </li>
          ))}
          <ImageViewer images={pictures.map((p) => ({ key: p.id, src: rawFileUrl(p.id), name: p.fileName, detail: p.id, download: { href: rawFileUrl(p.id), name: p.fileName } }))} index={viewing} onIndex={setViewing} />
        </ul>
      )}
      {played.length > 0 && (
        <ul className="mt-2 flex flex-wrap gap-2" aria-label="Sound and video">
          {played.map((f) => (
            <li key={f.id}>
              <MediaPlayer a={f} src={rawFileUrl(f.id)} />
            </li>
          ))}
        </ul>
      )}
    </>
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

interface AnswerProps {
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
  /** Sub-agents of task calls, as they work. */
  agents?: Record<string, AgentWork[]>
  diffs: Record<string, FileDiff>
  onDecide: (callId: string, answer: 'allow' | 'always' | 'deny') => void
  /** Puts part of this answer (chosen by keyboard, in a dialog) above the message box as a quote. */
  onQuote?: (quote: Quote) => void
  /** The question this answers (its id), and how to ask it again: as it was, shorter, longer, another model or thinking, the files as they were. */
  question?: string
  onAgain?: (question: string, change?: AgainChange & { files?: boolean }) => void
  /** Skip thinking, while the live answer thinks: it stops and the model answers. */
  onSkipThinking?: () => void
  /** Opens a sub-agent (by its task call's id) in the Tasks panel. */
  onOpenTask?: (callId: string) => void
}

/**
 * Whether an answer draws as it did: the same message objects, and the same of what it shows of the rest (its edits'
 * diffs; the questions, probabilities and progress of calls only while it is live). A finished turn of a long thread
 * is then not drawn again while another streams.
 */
function sameAnswer(a: AnswerProps, b: AnswerProps): boolean {
  if (a.live !== b.live || a.thinkingSince !== b.thinkingSince || a.notices !== b.notices || a.config !== b.config || a.onDecide !== b.onDecide || a.onQuote !== b.onQuote) return false
  if (a.question !== b.question || a.onAgain !== b.onAgain || a.onSkipThinking !== b.onSkipThinking || a.onOpenTask !== b.onOpenTask) return false
  if (a.answer.length !== b.answer.length || a.answer.some((m, i) => m !== b.answer[i])) return false
  if (b.live && (a.waiting !== b.waiting || a.always !== b.always || a.risks !== b.risks || a.calls !== b.calls || a.agents !== b.agents)) return false
  return b.answer.every((m) => m.role !== 'tool' || a.diffs[m.id] === b.diffs[m.id])
}

/**
 * Everything that answered a message: thinking, text, tool cards with their
 * diffs and questions, the files a finished turn changed, and a line with the
 * model, time and tokens (the share read from the cache with them), as Arena's
 * chat shows an answer.
 */
export const CodeAnswer = memo(function CodeAnswer({ answer, live, thinkingSince, notices, config, waiting, always, risks, calls, agents, diffs, onDecide, onQuote, question, onAgain, onSkipThinking, onOpenTask }: AnswerProps) {
  const [quoting, setQuoting] = useState(false)
  const results = new Map(answer.filter((m) => m.role === 'tool').map((m) => [m.toolCallId, m]))
  const assistants = answer.filter((m) => m.role === 'assistant')
  const text = assistants.map((a) => a.content).filter(Boolean).join('\n\n')
  const last = assistants.at(-1)
  const took = answer.reduce((n, m) => n + (m.durationMs ?? 0), 0)
  const usage = answerUsage(answer, config)
  const cached = usage.prompt > 0 ? Math.round((usage.cached / usage.prompt) * 100) : null
  const pending = waitsForModel(answer, live)
  const changed = live ? [] : turnFiles(answer, diffs)
  return (
    <section className={cn('min-w-0 animate-enter', live && 'pb-2')} aria-label="Answer" aria-busy={live}>
      {notices.map((n, i) => (
        <NoticeLine key={i} text={n.text} />
      ))}
      {assistants.map((a, i) => {
        const isLast = i === assistants.length - 1
        return (
          <div key={a.id} data-message={a.id} className="scroll-mt-4 rounded-lg">
            {a.reasoning && (
              <Thinking
                text={a.reasoning}
                live={live && isLast && !a.content && !a.toolCalls?.length}
                ms={a.thinkingMs}
                since={isLast ? thinkingSince : null}
                onHurry={live && isLast ? onSkipThinking : undefined}
                hurried={a.cutShort}
                hurryLabel="Skip thinking"
              />
            )}
            {a.content && (
              // A finished answer's words can be selected and quoted in a reply (the same element as it was written: nothing is drawn again).
              <div data-quote-source={live ? undefined : a.id}>
                <Markdown text={a.content} live={live && isLast} />
              </div>
            )}
            {live && isLast && a.content && <span className="ml-0.5 inline-block h-4 w-1.5 animate-pulse rounded-sm bg-primary align-middle" aria-hidden="true" />}
            {runsOf(a.toolCalls ?? []).map((run) => {
              const working = live && isLast
              if (run.kind === 'task') {
                // Sub-agents started together: one card, open while they work (their steps are in the Tasks panel).
                const rows = run.calls.map((t) => ({ t, result: results.get(t.id), work: workOf(results.get(t.id), agents?.[t.id]?.[0]) }))
                if (rows.length === 1) {
                  const { t, result, work } = rows[0]!
                  return <SubAgentLine key={t.id} callId={t.id} args={t.function.arguments} result={result} live={live} work={work} onOpen={onOpenTask} />
                }
                return (
                  <ToolRun
                    key={run.calls[0]!.id}
                    icon="agents"
                    working={working && rows.some((r) => !r.result)}
                    summary={subAgentsSummary(rows.map((r) => !!r.result), rows.length)}
                    forceOpen={false}
                  >
                    {rows.map(({ t, result, work }) => (
                      <SubAgentLine key={t.id} callId={t.id} args={t.function.arguments} result={result} live={live} work={work} onOpen={onOpenTask} />
                    ))}
                  </ToolRun>
                )
              }
              const asking = run.calls.some((t) => live && waiting.includes(t.id))
              const body = run.calls.map((t) => {
                const result = results.get(t.id)
                const waitingHere = live && waiting.includes(t.id)
                // A lone edit that went through is its diff below (the file, what changed, opened in the editor): no card of its own.
                if (run.calls.length === 1 && result?.status === 'complete' && !result.error && diffs[result.id]) return null
                return <ToolCard key={t.id} call={t} result={result} live={live} waiting={waitingHere} progress={calls?.[t.id]} agents={agents?.[t.id]} />
              })
              // What the person acts on or reads stays out of the fold: the file a call opened or wrote, what it made
              // (pictures, sounds), the changed lines of each edit, and an approval.
              const after = run.calls.map((t) => {
                const result = results.get(t.id)
                const diff = result ? diffs[result.id] : undefined
                const waitingHere = live && waiting.includes(t.id)
                const done = result && result.status === 'complete'
                return done || diff || waitingHere ? (
                  <div key={`after-${t.id}`}>
                    {done && !result.error && <OpenFile name={t.function.name} args={t.function.arguments} diff={diff} said={result.content} />}
                    {done && <MadeMedia said={result.content} />}
                    {diff && <DiffView diff={diff} live={live} />}
                    {waitingHere && <Approval name={t.function.name} always={always[t.id]} risk={risks?.[t.id]} onDecide={(answer) => onDecide(t.id, answer)} />}
                  </div>
                ) : null
              })
              return (
                <div key={run.calls[0]!.id}>
                  {run.calls.length === 1 ? (
                    body
                  ) : (
                    <ToolRun icon="tools" working={working && run.calls.some((t) => !results.get(t.id))} summary={toolsSummary(run.calls.map((t) => toolTitle(t.function.name)))} forceOpen={asking}>
                      {body}
                    </ToolRun>
                  )}
                  {after}
                </div>
              )
            })}
            {a.error && (
              <Alert variant="destructive" className="my-2">
                {a.error}
              </Alert>
            )}
            {/* Stopped by an admin, the error above says so and why. */}
            {a.status === 'stopped' && !a.error && (
              <p className="mt-1 flex items-center gap-1.5 text-xs text-muted-foreground">
                <Square className="size-3" aria-hidden="true" /> Stopped.
              </p>
            )}
          </div>
        )
      })}
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
      {changed.length > 0 && <TurnFiles files={changed} />}
      {!live && last && (
        <div className="mt-1 flex flex-wrap items-center gap-x-1 text-xs text-muted-foreground">
          {text && <CopyButton text={text} label="Copy answer" />}
          {text && onQuote && (
            <>
              <Tooltip content="Quote part of this answer…">
                <Button variant="ghost" size="icon-sm" className="size-7" onClick={() => setQuoting(true)} aria-label="Quote part of this answer…">
                  <TextQuote />
                </Button>
              </Tooltip>
              <QuotePartDialog text={text} open={quoting} onOpenChange={setQuoting} onQuote={onQuote} />
            </>
          )}
          {question && onAgain && (
            <AnswerAgainMenu
              config={config}
              busy={false}
              lengths={!!text}
              onAgain={(change) => onAgain(question, change)}
              more={
                // The turn changed files: asked again from where they were before it.
                changed.length > 0 && (
                  <>
                    <DropdownMenuSeparator />
                    <DropdownMenuItem onSelect={() => onAgain(question, { files: true })}>
                      <History /> Answer again, the files as they were
                    </DropdownMenuItem>
                  </>
                )
              }
            />
          )}
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
}, sameAnswer)
