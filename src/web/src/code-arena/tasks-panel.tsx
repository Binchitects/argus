import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Bot, Check, CircleSlash, CircleX, Clock, Network, PencilLine, Plus, Square, X } from 'lucide-react'
import { useEffect, useId, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { StepCard } from '@/pages/chat/agents'
import { seconds } from '@/pages/chat/format'
import { Markdown } from '@/pages/chat/markdown'
import { Thinking } from '@/pages/chat/parts'
import { PartHelp } from './help'
import { agentTaskQuery, agentTasksQuery, assistantsQuery, startAgentTask, stopAgentTask, taskOver, tokens, type AgentTask } from './tasks-api'

// The Tasks panel beside the chat, as Claude's desktop app has it: the session's sub-agents and the person's background
// tasks, each with its state, what it does now, its time and tokens; one opens to its own transcript, and stops.

/** Its time: from the server while over; counted on here, from when it started, while it runs. */
function useElapsed(task: Pick<AgentTask, 'ms' | 'started' | 'status'>): number | null {
  const running = task.status === 'running'
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!running) return
    const tick = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(tick)
  }, [running])
  if (running && task.started) return Math.max(task.ms ?? 0, now - Date.parse(task.started))
  return task.ms
}

const statusWords: Record<AgentTask['status'], string> = { queued: 'Waiting', running: 'Working', done: 'Done', failed: 'Failed', stopped: 'Stopped' }

export function TaskStatusIcon({ status }: { status: AgentTask['status'] }) {
  if (status === 'running') return <Spinner className="shrink-0 text-primary-ink" />
  if (status === 'done') return <Check className="size-4 shrink-0 text-success" aria-hidden="true" />
  if (status === 'failed') return <CircleX className="size-4 shrink-0 text-destructive-ink" aria-hidden="true" />
  if (status === 'stopped') return <CircleSlash className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
  return <Clock className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
}

/** Its time and tokens, in a few words. */
function facts(task: AgentTask, ms: number | null) {
  const spent = task.tokens.prompt + task.tokens.completion
  return [ms != null && seconds(ms), spent > 0 && `${tokens(spent)} tokens`, task.calls > 0 && `${task.calls} tool call${task.calls === 1 ? '' : 's'}`].filter(Boolean).join(' · ')
}

async function stop(task: AgentTask, after: () => void) {
  try {
    await stopAgentTask(task.id)
    toast.success(`Stopping ${task.title}.`)
  } catch (e) {
    toast.error(errorMessage(e))
  } finally {
    after()
  }
}

function TaskRow({ task, onOpen, onStopped }: { task: AgentTask; onOpen: () => void; onStopped: () => void }) {
  const ms = useElapsed(task)
  const over = taskOver(task)
  return (
    <li className="group relative min-w-0">
      <button
        type="button"
        onClick={onOpen}
        aria-label={`${task.title}: ${statusWords[task.status]}`}
        className={cn(
          'grid w-full min-w-0 grid-cols-[auto_minmax(0,1fr)] gap-x-2 gap-y-0.5 rounded-md border bg-card px-2.5 py-2 text-left text-sm transition-colors outline-none hover:bg-accent/60 focus-visible:ring-[3px] focus-visible:ring-ring',
          task.status === 'running' && 'border-primary/40',
          !over && 'pr-9',
        )}
      >
        <span className="row-span-2 pt-0.5">
          <TaskStatusIcon status={task.status} />
        </span>
        <span className="flex min-w-0 items-center gap-1.5">
          <bdi className="truncate font-medium">{task.title}</bdi>
          {task.kind === 'background' && <Badge variant="secondary" className="shrink-0 px-1.5 py-0 text-[0.625rem]">yours</Badge>}
          {task.writes && (
            <Tooltip content="May change files: runs alone among those that do">
              <PencilLine className="size-3.5 shrink-0 text-muted-foreground" aria-label="May change files" />
            </Tooltip>
          )}
        </span>
        <span className="truncate text-xs text-muted-foreground tabular-nums">
          {[task.as && `as ${task.as}`, task.doing ?? (over ? statusWords[task.status] : null), facts(task, ms)].filter(Boolean).join(' · ')}
        </span>
      </button>
      {!over && !task.kept && (
        <Tooltip content="Stop it">
          <Button variant="ghost" size="icon-sm" className="absolute top-1.5 right-1.5 size-7" aria-label={`Stop ${task.title}`} onClick={() => void stop(task, onStopped)}>
            <Square className="size-3.5" />
          </Button>
        </Tooltip>
      )}
    </li>
  )
}

/** One task, opened: what it was asked, its tools, its thinking, its tool calls with their results, and its report. */
function TaskDetail({ id, onBack, onStopped }: { id: string; onBack: () => void; onStopped: () => void }) {
  const q = useQuery(agentTaskQuery(id))
  const task = q.data
  const ms = useElapsed(task ?? { ms: null, started: null, status: 'queued' })
  const live = !!task && !taskOver(task)
  const work = task?.work
  const asked = useId()
  const reported = useId()
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex shrink-0 items-center gap-1 border-b px-2 py-1.5">
        <Button variant="ghost" size="icon-sm" onClick={onBack} aria-label="Back to the tasks">
          <ArrowLeft />
        </Button>
        <h3 className="min-w-0 flex-1 truncate text-sm font-semibold">{task?.title ?? 'Task'}</h3>
        {task && live && !task.kept && (
          <Button variant="outline" size="sm" className="h-7" onClick={() => void stop(task, onStopped)}>
            <Square className="size-3.5" /> Stop
          </Button>
        )}
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-3 py-3" aria-busy={live}>
        {q.isPending && <Skeleton className="h-24" />}
        {q.error && <Alert variant="destructive">{errorMessage(q.error)}</Alert>}
        {task && (
          // A word longer than the panel (a path, a token) breaks rather than pushing the panel sideways.
          <div className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-3 [overflow-wrap:anywhere]">
            <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground tabular-nums">
              <TaskStatusIcon status={task.status} />
              <span>{statusWords[task.status]}</span>
              <span>{task.kind === 'background' ? 'Yours, in the background' : 'Started by the agent'}</span>
              {task.as && <span>as {task.as}</span>}
              {task.model && <span>{task.model}</span>}
              {facts(task, ms) && <span>{facts(task, ms)}</span>}
            </p>
            <section aria-labelledby={asked} className="grid gap-1">
              <h4 id={asked} className="text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">
                What it was asked
              </h4>
              <p dir="auto" className="border-s-2 ps-3 text-sm whitespace-pre-wrap text-muted-foreground">
                {task.prompt}
              </p>
            </section>
            {task.tools.length > 0 && (
              <p className="text-xs text-muted-foreground">
                <span className="font-medium text-foreground">Tools:</span> {task.tools.join(', ')}
              </p>
            )}
            {work?.reasoning && <Thinking text={work.reasoning} live={live && !work.text} ms={null} since={null} />}
            {(work?.steps ?? []).map((s) => (
              <StepCard key={s.id} step={s} live={live} />
            ))}
            {live && task.doing && <p className="text-xs text-muted-foreground text-shimmer">{task.doing}…</p>}
            {(task.report || work?.text) && (
              <section aria-labelledby={reported} className="grid min-w-0 gap-1">
                <h4 id={reported} className="text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">
                  Its report
                </h4>
                <Markdown text={task.report ?? work?.text ?? ''} live={live} />
              </section>
            )}
            {task.error && task.status !== 'done' && <Alert variant={task.status === 'stopped' ? 'warning' : 'destructive'}>{task.error}</Alert>}
            {task.kept && <p className="text-xs text-muted-foreground">Kept with the session from an earlier run.</p>}
          </div>
        )}
      </div>
    </div>
  )
}

/** A task of the person's: what to do, as which assistant, whether it may change files. */
function NewTask({ onStarted, onCancel, plan }: { onStarted: (task: AgentTask) => void; onCancel: () => void; plan: boolean }) {
  const [prompt, setPrompt] = useState('')
  const [title, setTitle] = useState('')
  const [assistant, setAssistant] = useState('')
  const [edits, setEdits] = useState(false)
  const [busy, setBusy] = useState(false)
  const assistants = useQuery(assistantsQuery)
  const start = async () => {
    setBusy(true)
    try {
      onStarted(await startAgentTask({ prompt: prompt.trim(), title: title.trim() || undefined, assistant: assistant || undefined, edits }))
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <form
      aria-label="New task"
      className="grid gap-3 border-b px-3 py-3"
      onSubmit={(e) => {
        e.preventDefault()
        if (prompt.trim()) void start()
      }}
    >
      <Field label="What to do">
        <Textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} rows={4} maxLength={20_000} placeholder="Find where the config is read, and what reads it." autoFocus />
      </Field>
      <Field label="Its name" hint="A few words for this list (optional).">
        <Input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={80} />
      </Field>
      {(assistants.data?.assistants?.length ?? 0) > 0 && (
        <Field label="As an assistant">
          <Select value={assistant || 'none'} onValueChange={(v) => setAssistant(v === 'none' ? '' : v)}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="none">None: Code Arena's own</SelectItem>
              {assistants.data!.assistants!.map((a) => (
                <SelectItem key={a.id} value={a.id}>
                  {a.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      )}
      <div className="flex items-start gap-2 text-sm">
        <Switch id="new-task-may-edit" checked={edits} onCheckedChange={setEdits} disabled={plan} aria-describedby="new-task-edits" />
        <span className="grid gap-0.5">
          <label htmlFor="new-task-may-edit">May change files and run commands</label>
          <span id="new-task-edits" className="text-xs text-muted-foreground">
            {plan ? 'Not in plan mode.' : 'As the mode allows, never asking: what it would ask is refused. One such task runs at a time.'}
          </span>
        </span>
      </div>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" size="sm" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" size="sm" disabled={!prompt.trim() || busy}>
          {busy ? <Spinner /> : <Plus />} Start
        </Button>
      </div>
    </form>
  )
}

/**
 * The panel: the tasks newest first, those at work at the top; one chosen shows its transcript. `selected` is a task's id
 * or its call's (the chat's line opens it so).
 */
export function TasksPanel({ selected, onSelect, onClose, plan }: { selected: string | null; onSelect: (id: string | null) => void; onClose: () => void; plan: boolean }) {
  const queryClient = useQueryClient()
  const list = useQuery(agentTasksQuery)
  const [adding, setAdding] = useState(false)
  const tasks = list.data?.tasks ?? []
  // A task by its id, or by its call's (code-arena finds it by either).
  const shown = selected
  const working = tasks.filter((t) => !taskOver(t)).reverse()
  const ended = tasks.filter(taskOver).reverse()
  const refresh = () => void queryClient.invalidateQueries({ queryKey: agentTasksQuery.queryKey })
  return (
    <aside aria-label="Tasks" className="flex h-full min-h-0 w-full min-w-0 flex-col bg-background">
      <header className="flex h-11 shrink-0 items-center gap-1 border-b px-2">
        <Network className="ml-1 size-4 text-muted-foreground" aria-hidden="true" />
        <h2 className="text-sm font-semibold">Tasks</h2>
        {working.length > 0 && <span className="text-xs text-muted-foreground tabular-nums">{working.length} at work</span>}
        <span className="ml-auto" />
        <Tooltip content="A task of yours, in the background">
          <Button variant="ghost" size="icon-sm" aria-label="New task" aria-pressed={adding} onClick={() => setAdding(!adding)}>
            <Plus />
          </Button>
        </Tooltip>
        <PartHelp part="tasks" className="size-8 rounded-md [&_svg]:size-4" />
        {/* With a task open, closing goes back to the list; from the list, it closes the panel. */}
        <Tooltip content={shown ? 'Close the task' : 'Close the tasks'}>
          <Button variant="ghost" size="icon-sm" onClick={() => (shown ? onSelect(null) : onClose())} aria-label={shown ? 'Close the task' : 'Close the tasks'}>
            <X />
          </Button>
        </Tooltip>
      </header>
      {adding && (
        <NewTask
          plan={plan}
          onCancel={() => setAdding(false)}
          onStarted={(task) => {
            setAdding(false)
            refresh()
            onSelect(task.id)
          }}
        />
      )}
      {shown ? (
        <TaskDetail id={shown} onBack={() => onSelect(null)} onStopped={refresh} />
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2">
          {list.isPending && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="mb-2 h-12" />)}
          {list.error && <Alert variant="destructive">{errorMessage(list.error)}</Alert>}
          {list.data && tasks.length === 0 && (
            <div className="grid justify-items-center gap-2 px-4 py-8 text-center text-sm text-muted-foreground">
              <Bot className="size-6" aria-hidden="true" />
              <p>No tasks in this session yet.</p>
              <p className="text-xs">The agent hands work to sub-agents as it goes; each shows here, not in the chat. Start one of your own with +.</p>
            </div>
          )}
          {working.length > 0 && (
            <ul aria-label="At work" className="mb-3 grid gap-1.5">
              {working.map((t) => (
                <TaskRow key={t.id} task={t} onOpen={() => onSelect(t.id)} onStopped={refresh} />
              ))}
            </ul>
          )}
          {ended.length > 0 && (
            <>
              <p className="px-1 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Ended</p>
              <ul aria-label="Ended" className="grid gap-1.5">
                {ended.map((t) => (
                  <TaskRow key={t.id} task={t} onOpen={() => onSelect(t.id)} onStopped={refresh} />
                ))}
              </ul>
            </>
          )}
        </div>
      )}
    </aside>
  )
}
