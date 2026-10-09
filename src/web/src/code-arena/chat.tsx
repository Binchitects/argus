import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, ChevronDown, FolderGit2, GitBranch, History, ListChecks, LoaderCircle, MessageSquarePlus, Monitor, Moon, PanelRightClose, ScrollText, ShieldCheck, Square, Sun, TestTubeDiagonal } from 'lucide-react'
import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError, errorMessage } from '@/lib/api'
import { useTheme, type ThemePreference } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { configQuery, streamChat } from '@/pages/chat/api'
import { contextOf } from '@/pages/chat/context'
import { ContextGauge } from '@/pages/chat/context-gauge'
import { answerNews, bucket } from '@/pages/chat/format'
import { ModelPicker, ThinkingPicker } from '@/pages/chat/header'
import { stopped, withQuestion } from '@/pages/chat/live'
import { toTurns } from '@/pages/chat/tree'
import { CompactedMark, QuestionTurn } from '@/pages/chat/turns'
import type { ChatConfig } from '@/pages/chat/types'
import {
  answerApproval,
  changeSettings,
  fromSession,
  inOrder,
  modeLabels,
  newSession,
  reduceCode,
  resumeSession,
  sessionQuery,
  sessionsQuery,
  stateQuery,
  stopJob,
  stopTurn,
  type CodeEvent,
  type CodeLive,
  type CodeState,
  type LiveJob,
  type Mode,
  type SessionSummary,
} from './api'
import { CodeAnswer } from './answer'
import { PartHelp } from './help'
import { changesQuery, savePreferences } from './ide-api'

// The agent's chat, in the IDE's side panel: the folder's sessions (the side
// bar's Chat view), the thread, the composer, the model and the mode.

/** The id a message is shown under until the server gives it its place. */
let localCount = 0
const newLocalId = () => `local-${Date.now()}-${++localCount}`

/** This folder's saved sessions, newest first, by when; one opens with its history. */
export function Sessions({ state, onNavigate }: { state: CodeState; onNavigate?: () => void }) {
  const queryClient = useQueryClient()
  const list = useQuery(sessionsQuery)
  const groups = new Map<string, SessionSummary[]>()
  for (const s of list.data ?? []) groups.set(bucket(s.updatedAt), [...(groups.get(bucket(s.updatedAt)) ?? []), s])
  const open = async (go: () => Promise<unknown>) => {
    try {
      await go()
      onNavigate?.()
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['code'] })])
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <nav aria-label="Sessions" className="flex h-full min-h-0 flex-col">
      <div className="flex h-14 shrink-0 items-center gap-2.5 border-b border-sidebar-border px-4">
        <img src="/favicon.svg" alt="" className="size-7 rounded-md" />
        <div className="grid min-w-0">
          <span className="truncate text-sm font-semibold">Code Arena</span>
          <span className="truncate text-xs text-muted-foreground" title={state.folder}>
            {state.project}
          </span>
        </div>
        <PartHelp part="sessions" className="ml-auto" />
      </div>
      <div className="grid gap-2 p-3">
        <Button onClick={() => void open(newSession)} className="justify-start" disabled={state.busy}>
          <MessageSquarePlus /> New session
        </Button>
      </div>
      <div className="min-h-0 flex-1 overflow-x-hidden overflow-y-auto px-2 pb-3">
        {list.isPending && Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="mx-1 mb-2 h-7" />)}
        {list.data?.length === 0 && <p className="px-2 py-4 text-sm text-muted-foreground">No sessions in this folder yet.</p>}
        {[...groups].map(([title, items]) => (
          <div key={title} className="mb-3 min-w-0">
            <p className="px-2 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{title}</p>
            <ul className="flex min-w-0 flex-col gap-0.5">
              {items.map((s) => {
                const active = s.id === state.session
                return (
                  <li key={s.id} className="min-w-0 animate-enter">
                    <button
                      type="button"
                      onClick={() => !active && void open(() => resumeSession(s.id))}
                      disabled={state.busy && !active}
                      aria-current={active ? 'page' : undefined}
                      title={`${s.title} · ${s.messages} messages · ${s.id}`}
                      className={cn(
                        'block w-full truncate rounded-md px-2 py-1.5 text-left text-sm transition-colors duration-150 outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring disabled:opacity-60',
                        active ? 'bg-accent font-medium text-foreground' : 'text-foreground/85',
                      )}
                    >
                      {active && state.busy && (
                        <>
                          <span className="mr-1.5 inline-block size-1.5 animate-pulse rounded-full bg-primary align-middle" aria-hidden="true" />
                          <span className="sr-only">Answering: </span>
                        </>
                      )}
                      <bdi>{s.title}</bdi>
                    </button>
                  </li>
                )
              })}
            </ul>
          </div>
        ))}
      </div>
      <div className="grid gap-0.5 border-t border-sidebar-border px-4 py-2 text-[0.6875rem] text-muted-foreground">
        <span className="flex min-w-0 items-center gap-1.5" title={state.folder}>
          <FolderGit2 className="size-3.5 shrink-0" aria-hidden="true" /> <span className="truncate">{state.folder}</span>
        </span>
        {state.branch && (
          <span className="flex items-center gap-1.5">
            <GitBranch className="size-3.5 shrink-0" aria-hidden="true" /> {state.branch}
          </span>
        )}
        <span aria-label="Version">code-arena {state.version}</span>
      </div>
    </nav>
  )
}

/** Light, dark, or the system's; kept by code-arena, so the next run (on another port) opens in it too. */
export function ThemeMenu({ side = 'bottom', align = 'end' }: { side?: 'right' | 'bottom' | 'top'; align?: 'start' | 'end' }) {
  const { preference, resolved, setPreference: setTheme } = useTheme()
  const setPreference = (theme: ThemePreference) => {
    setTheme(theme)
    void savePreferences({ theme }).catch(() => undefined)
  }
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-sm" aria-label="Theme">
          {resolved === 'dark' ? <Moon /> : <Sun />}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent side={side} align={align}>
        <DropdownMenuLabel>Theme</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={preference} onValueChange={(v) => setPreference(v as ThemePreference)}>
          <DropdownMenuRadioItem value="light">
            <Sun /> Light
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="dark">
            <Moon /> Dark
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="system">
            <Monitor /> System
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}


/** How much runs without asking: ask, auto-edit, plan, yolo. */
function ModePicker({ state, onChange }: { state: CodeState; onChange: (mode: Mode) => void }) {
  return (
    <Select value={state.mode} onValueChange={(v) => onChange(v as Mode)}>
      <SelectTrigger
        size="sm"
        className={cn('h-8 w-auto shrink-0 gap-1.5 rounded-full border-transparent bg-transparent px-2.5 shadow-none hover:bg-accent', state.mode === 'yolo' && 'text-destructive-ink', state.mode === 'plan' && 'text-primary-ink')}
        aria-label="Mode"
      >
        <ShieldCheck className="size-4" aria-hidden="true" />
        <SelectValue>{modeLabels[state.mode]}</SelectValue>
      </SelectTrigger>
      <SelectContent>
        {state.modes.map((m) => (
          <SelectItem key={m.name} value={m.name} textValue={modeLabels[m.name]}>
            <span className="grid">
              <span className="font-medium">{modeLabels[m.name]}</span>
              <span className="text-xs text-muted-foreground">{m.description}</span>
            </span>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

const suggestions = [
  { icon: TestTubeDiagonal, text: 'How is this project built and tested? Run the tests and tell me what fails.' },
  { icon: ListChecks, text: 'Find the TODOs and FIXMEs in the code and sum them up by area.' },
  { icon: ScrollText, text: 'Review the uncommitted changes (git diff) and point out problems.' },
]

/**
 * The conversation with the agent, as Arena's chat shows one. The IDE hears
 * each event (an edit's diff: the file reloads) and the end of each turn.
 */
export function Thread({
  state,
  config,
  onOpenList,
  onHide,
  onEvent,
  onTurnEnd,
}: {
  state: CodeState
  config: ChatConfig
  onOpenList: () => void
  onHide?: () => void
  onEvent?: (e: CodeEvent) => void
  onTurnEnd?: () => void
}) {
  const queryClient = useQueryClient()
  const session = useQuery(sessionQuery)
  const [live, setLive] = useState<CodeLive | null>(null)
  const [streaming, setStreaming] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const watching = useRef(false)
  const scroller = useRef<HTMLDivElement>(null)
  const [atBottom, setAtBottom] = useState(true)

  const view = live ?? fromSession(session.data)
  const path = inOrder(view.messages)
  const turns = toTurns(path)
  const model = config.models.find((m) => m.name === state.model)
  const answering = streaming && view.mode !== 'compact'
  const lastUsage = [...path].reverse().find((m) => m.role === 'assistant' && m.promptTokens != null)
  const context = contextOf(lastUsage, state.context, null)

  useEffect(() => {
    const el = scroller.current
    if (atBottom && el) el.scrollTop = el.scrollHeight
  }, [view.messages, atBottom])

  /**
   * What a turn changes, read again: the state and the session are waited for (the thread shows the saved turn
   * from them), the agent's changes, the files and the session list come when they come. The search is left as
   * it is: it would read every file again after each answer.
   */
  const refresh = useCallback(async () => {
    for (const queryKey of [changesQuery.queryKey, ['code', 'files'], sessionsQuery.queryKey]) void queryClient.invalidateQueries({ queryKey })
    await Promise.all([queryClient.invalidateQueries({ queryKey: stateQuery.queryKey }), queryClient.invalidateQueries({ queryKey: sessionQuery.queryKey })])
  }, [queryClient])

  /** Streams a turn (or a compaction, or the turn running when the page opened) into the thread, then reads the saved session. */
  const run = useCallback(
    async (path: string, body: object | null, start: CodeLive, localId: string | null, watch?: (e: CodeEvent) => void): Promise<boolean> => {
      watching.current = true
      setLive(start)
      setStreaming(true)
      setError(null)
      setAtBottom(true)
      let received = false
      try {
        await streamChat(
          path,
          body,
          (e) => {
            received = true
            const event = e as CodeEvent
            watch?.(event)
            onEvent?.(event)
            setLive((s) => reduceCode(s ?? start, event, localId))
          },
          new AbortController().signal,
        )
      } catch (err) {
        setError(err instanceof ApiError ? err.message : received ? 'The answer was interrupted: is code-arena web still running?' : 'The message did not reach code-arena web. It is back in the box below.')
        setLive((s) => (s ? { ...s, ...stopped(s) } : s))
      } finally {
        await refresh()
        setStreaming(false)
        setLive(null)
        watching.current = false
        onTurnEnd?.()
      }
      return received
    },
    [refresh, onEvent, onTurnEnd],
  )

  // A turn running when the page opened (it was reloaded, or another tab asked): watched from its start.
  useEffect(() => {
    if (!session.data?.busy || watching.current) return
    void run('/api/turn', null, fromSession(session.data), null)
  }, [session.data, run])

  const send = async (text: string): Promise<boolean> => {
    if (text === '/compact') return compact()
    if (text === '/clear' || text === '/new') {
      await newSession().catch((e) => toast.error(errorMessage(e)))
      await refresh()
      return true
    }
    const localId = newLocalId()
    return run('/api/messages', { text }, { ...view, ...withQuestion(view, localId, path.at(-1)?.id ?? null, text, []) }, localId)
  }

  const compact = async (): Promise<boolean> => {
    if (streaming || path.length < 2) {
      toast.error(streaming ? 'Compact when the answer is done.' : 'Nothing to compact yet.')
      return false
    }
    let said: string | null = null
    let failed: string | null = null
    await run('/api/compact', {}, { ...view, notices: [], mode: 'compact' }, null, (e) => {
      if (e.type === 'notice') said = e.text
      if (e.type === 'error') failed = e.message
    })
    if (failed) toast.error(failed)
    else if (said) toast.success(said)
    return true
  }

  const decide = async (callId: string, answer: 'allow' | 'always' | 'deny') => {
    setLive((s) => (s ? { ...s, waiting: (s.waiting ?? []).filter((w) => w !== callId) } : s))
    await answerApproval(callId, answer).catch((e) => toast.error(errorMessage(e)))
  }

  const settings = async (change: Parameters<typeof changeSettings>[0]) => {
    try {
      queryClient.setQueryData(stateQuery.queryKey, await changeSettings(change))
      void queryClient.invalidateQueries({ queryKey: configQuery.queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  const stop = () => void stopTurn().catch((e) => !(e instanceof ApiError && e.http === 409) && toast.error(errorMessage(e)))

  const empty = turns.length === 0
  const lastTurn = turns.length - 1
  const composer = (big: boolean) => (
    <Composer
      streaming={streaming}
      onSend={send}
      onStop={stop}
      big={big}
      mode={<ModePicker state={state} onChange={(mode) => void settings({ mode })} />}
      context={context ? <ContextGauge context={context} onCompact={!streaming && path.length >= 2 ? () => void compact() : undefined} busy={streaming} /> : null}
    />
  )

  return (
    <section aria-label="Chat" className="@container flex h-full min-h-0 min-w-0 flex-col bg-background">
      <header className="flex h-11 shrink-0 items-center gap-1 border-b px-2">
        <ModelPicker config={config} value={state.model} onChange={(m) => m && m !== state.model && void settings({ model: m })} />
        {model?.thinking && <ThinkingPicker config={config} value={state.thinking} onChange={(level) => void settings({ thinking: level })} />}
        <span className="ml-auto" />
        {state.mode === 'yolo' && <span className="hidden text-xs text-destructive-ink @md:inline">Yolo: nothing asks</span>}
        <PartHelp part="chat" className="size-8 rounded-md [&_svg]:size-4" />
        <Tooltip content="This folder's sessions">
          <Button variant="ghost" size="icon-sm" onClick={onOpenList} aria-label="Sessions">
            <History />
          </Button>
        </Tooltip>
        {onHide && (
          <Tooltip content="Hide the chat">
            <Button variant="ghost" size="icon-sm" onClick={onHide} aria-label="Hide the chat">
              <PanelRightClose />
            </Button>
          </Tooltip>
        )}
      </header>
      {empty ? (
        <div className="flex min-h-0 flex-1 flex-col items-center justify-center overflow-y-auto px-4 py-6">
          <div className="w-full max-w-2xl animate-enter">
            <h1 className="mb-2 text-center text-xl font-semibold tracking-tight">What should we work on?</h1>
            <p className="mb-6 text-center text-muted-foreground">
              Code Arena reads and changes the code in <span className="font-mono text-foreground">{state.project}</span>, runs commands
              {state.arenaTools ? ", and uses Arena's tools" : ''}. Mode {modeLabels[state.mode]}: {state.modes.find((m) => m.name === state.mode)?.description}.
            </p>
            {error && (
              <Alert variant="destructive" className="mb-3">
                {error}
              </Alert>
            )}
            {composer(true)}
            <div className="stagger mt-4 grid gap-2 @3xl:grid-cols-3">
              {suggestions.map((s) => (
                <button
                  key={s.text}
                  type="button"
                  disabled={streaming}
                  onClick={() => void send(s.text)}
                  className="flex items-start gap-2 rounded-xl border bg-card p-3 text-left text-sm text-muted-foreground transition-[color,border-color,box-shadow,translate] duration-200 outline-none hover:-translate-y-0.5 hover:border-primary/40 hover:text-foreground hover:shadow-md focus-visible:ring-[3px] focus-visible:ring-ring"
                >
                  <s.icon className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden="true" />
                  {s.text}
                </button>
              ))}
            </div>
          </div>
        </div>
      ) : (
        <>
          <div className="relative flex min-h-0 flex-1 flex-col">
            <output className="sr-only">{answerNews(answering, path)}</output>
            <div
              ref={scroller}
              onScroll={() => {
                const el = scroller.current
                if (el) setAtBottom(el.scrollHeight - el.scrollTop - el.clientHeight < 80)
              }}
              className="relative min-h-0 flex-1 overflow-y-auto"
            >
              <div className="mx-auto grid w-full max-w-(--thread-max) gap-6 px-4 py-6 @lg:px-6">
                {turns.map((t, i) => {
                  const isLive = answering && i === lastTurn
                  // The older conversation, summarized to fit the model's window: shown folded, as the chat shows a compaction.
                  if (t.question?.summary) return <CompactedMark key={t.question.id} summary={t.question.summary} onOpenFile={() => undefined} />
                  return (
                    <div key={t.question?.id ?? t.answer[0]?.id ?? i} className="grid gap-4">
                      {t.question && <QuestionTurn m={t.question} siblings={[t.question]} busy={streaming} onSwitch={() => undefined} />}
                      {(t.answer.length > 0 || isLive) && (
                        <CodeAnswer
                          answer={t.answer}
                          live={isLive}
                          thinkingSince={isLive ? view.thinkingSince : null}
                          notices={i === lastTurn ? view.notices : []}
                          config={config}
                          waiting={isLive ? (view.waiting ?? []) : []}
                          always={view.always}
                          risks={view.risks}
                          calls={isLive ? view.calls : undefined}
                          diffs={view.diffs}
                          onDecide={(id, answer) => void decide(id, answer)}
                        />
                      )}
                    </div>
                  )
                })}
                {streaming && view.mode === 'compact' && (
                  <output className="flex items-center gap-2 text-sm text-muted-foreground">Compacting: the model summarizes the conversation so the next answers read the summary…</output>
                )}
                {error && <Alert variant="destructive">{error}</Alert>}
              </div>
            </div>
          </div>
          <div className="relative mx-auto w-full max-w-(--thread-max) px-4 pb-4 @lg:px-6">
            {!atBottom && (
              <Button
                variant="outline"
                size="icon-sm"
                className="absolute -top-12 left-1/2 -translate-x-1/2 animate-pop rounded-full shadow-md"
                onClick={() => {
                  setAtBottom(true)
                  scroller.current?.scrollTo?.({ top: scroller.current.scrollHeight, behavior: 'smooth' })
                }}
                aria-label="Jump to the latest"
              >
                <ArrowDown />
              </Button>
            )}
            {view.jobs.length > 0 && <Jobs jobs={view.jobs} />}
            {composer(false)}
          </div>
        </>
      )}
    </section>
  )
}

/**
 * The commands the agent runs with no time limit in this turn: the end of each
 * one's output as it comes, how it ended, and Stop. The turn waits for them;
 * the agent is told how each ended.
 */
function Jobs({ jobs }: { jobs: LiveJob[] }) {
  return (
    <section aria-label="Commands with no time limit" className="mb-2 grid max-h-[45vh] gap-2 overflow-y-auto">
      {jobs.map((j) => (
        <JobBox key={j.id} job={j} />
      ))}
    </section>
  )
}

function JobBox({ job }: { job: LiveJob }) {
  const [open, setOpen] = useState(true)
  const [stopping, setStopping] = useState(false)
  const out = useRef<HTMLPreElement>(null)
  // The last 200 lines; the agent reads all of it with command_output.
  const text = job.output.split('\n').slice(-200).join('\n').trimEnd()
  useEffect(() => {
    const el = out.current
    if (el) el.scrollTop = el.scrollHeight
  }, [text, open])
  const stop = async () => {
    setStopping(true)
    try {
      await stopJob(job.id)
    } catch (e) {
      setStopping(false)
      toast.error(errorMessage(e))
    }
  }
  return (
    <div className="min-w-0 animate-enter rounded-xl border bg-card text-sm">
      <div className="flex min-w-0 items-center gap-2 px-3 py-1.5">
        {job.running ? (
          <LoaderCircle className="size-3.5 shrink-0 animate-spin text-primary motion-reduce:animate-none" aria-hidden="true" />
        ) : (
          <span className={cn('size-2 shrink-0 rounded-full', job.failed ? 'bg-destructive' : 'bg-success')} aria-hidden="true" />
        )}
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          aria-label={`job ${job.id}: ${job.command}`}
          title={job.command}
          className="flex min-w-0 flex-1 items-center gap-1.5 rounded-sm text-left outline-none focus-visible:ring-[3px] focus-visible:ring-ring"
        >
          <ChevronDown className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform', !open && '-rotate-90')} aria-hidden="true" />
          <span className="shrink-0 text-xs text-muted-foreground">job {job.id}</span>
          <span className="truncate font-mono text-xs">{job.command}</span>
        </button>
        <span className={cn('shrink-0 text-xs', job.failed ? 'text-destructive-ink' : 'text-muted-foreground')}>{job.running ? 'running, no time limit' : job.status}</span>
        {job.running && (
          <Button size="sm" variant="outline" className="h-7 shrink-0" onClick={() => void stop()} disabled={stopping} aria-label={`Stop job ${job.id}`}>
            <Square className="fill-current" /> {stopping ? 'Stopping…' : 'Stop'}
          </Button>
        )}
      </div>
      {open && (
        <pre ref={out} aria-label={`Output of job ${job.id}`} className="max-h-48 overflow-auto border-t bg-muted/40 px-3 py-2 font-mono text-xs leading-relaxed whitespace-pre-wrap break-all">
          {text || 'No output yet.'}
        </pre>
      )}
    </div>
  )
}

/**
 * Where the message is written, as in the chat: Enter sends, Shift+Enter adds a
 * line. /compact summarizes the conversation, /clear starts a new session. A
 * message that did not reach the server comes back into the box.
 */
function Composer({ streaming, onSend, onStop, big, mode, context }: { streaming: boolean; onSend: (text: string) => Promise<boolean>; onStop: () => void; big: boolean; mode: ReactNode; context: ReactNode }) {
  const [text, setText] = useState('')
  const area = useRef<HTMLTextAreaElement>(null)
  useEffect(() => {
    const el = area.current
    if (!el) return
    el.style.height = 'auto'
    // Empty: its rows say how tall (the placeholder is not measured, which a page still laying out can make tall).
    if (text) el.style.height = `${Math.min(el.scrollHeight, 280)}px`
  }, [text])
  const canSend = !streaming && text.trim().length > 0
  const submit = async () => {
    if (!canSend) return
    const sent = text.trim()
    setText('')
    if (!(await onSend(sent))) setText((now) => now || sent)
    area.current?.focus()
  }
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault()
      void submit()
    }
  }
  return (
    <form
      className={cn('relative rounded-2xl border bg-card shadow-sm transition-shadow focus-within:border-primary/50 focus-within:shadow-md', big && 'shadow-md')}
      onSubmit={(e) => {
        e.preventDefault()
        void submit()
      }}
    >
      <textarea
        ref={area}
        dir="auto"
        rows={big ? 3 : 1}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKey}
        placeholder={streaming ? 'Code Arena is working… (Stop to interrupt)' : 'Ask Code Arena to read, change or run something'}
        aria-label="Message"
        autoFocus
        className={cn('block max-h-72 w-full resize-none bg-transparent px-4 pt-3 text-[0.9375rem] leading-relaxed outline-none placeholder:text-muted-foreground', big && 'min-h-20')}
      />
      <div className="flex items-center gap-2 px-2 pt-1 pb-2">
        {mode}
        <span className="hidden text-xs text-muted-foreground @2xl:inline">Enter to send · Shift+Enter for a new line · /compact · /clear</span>
        <span className="ml-auto" />
        {context}
        {streaming ? (
          <Button type="button" size="icon-sm" variant="secondary" className="animate-pop rounded-full" onClick={onStop} aria-label="Stop">
            <Square className="fill-current" />
          </Button>
        ) : (
          <Button type="submit" size="icon-sm" className="animate-pop rounded-full" disabled={!canSend} aria-label="Send">
            <ArrowUp />
          </Button>
        )}
      </div>
    </form>
  )
}

