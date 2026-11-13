import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bot, CalendarClock, Play, Plus, Power, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { PartHelp } from './help'
import { api } from './http'
import { automationQuery, type AgentRun, type ArenaAgent, type Automation } from './automation-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'
const heading = 'text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase'
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' }) : '')

/**
 * The side bar's Automation: Code Arena's own schedules (a prompt run in a folder every so often, even with Code Arena
 * closed once an OS timer runs them), and Arena's agents and scheduled tasks, each run from here and followed.
 */
export function AutomationView() {
  const queryClient = useQueryClient()
  const state = useQuery(automationQuery)
  const act = useMutation({
    mutationFn: (body: Record<string, unknown>) => api<Automation>('/api/automation', { body }),
    onSuccess: (data) => queryClient.setQueryData(automationQuery.queryKey, data),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const [adding, setAdding] = useState(false)
  const data = state.data
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className={cn('min-w-0 flex-1 truncate', heading)}>Automation</h2>
        <Tooltip content="New schedule">
          <button type="button" className={action} aria-label="New schedule" onClick={() => setAdding(true)}>
            <Plus />
          </button>
        </Tooltip>
        <PartHelp part="automation" />
      </div>
      <div className="grid min-h-0 flex-1 content-start gap-4 overflow-auto px-3 pb-3 text-xs">
        {state.isPending && <p className="text-muted-foreground">Reading…</p>}
        {state.error && <p role="alert" className="text-destructive-ink">{errorMessage(state.error)}</p>}
        {data && (
          <>
            <section aria-label="Schedules here" className="grid gap-1.5">
              <h3 className={heading}>Schedules here</h3>
              {adding && <NewSchedule folder={data.folder} saving={act.isPending} onCancel={() => setAdding(false)} onAdd={(schedule) => act.mutate({ schedule }, { onSuccess: () => setAdding(false) })} />}
              {data.schedules.length === 0 && !adding && (
                <p className="text-muted-foreground">
                  None yet. <button type="button" className="text-primary-ink underline-offset-2 hover:underline" onClick={() => setAdding(true)}>New schedule</button>: a prompt run in a folder every so often, as a session of its own.
                </p>
              )}
              <ul className="grid gap-1">
                {data.schedules.map((s) => (
                  <li key={s.name} aria-label={s.name} className="grid gap-0.5 rounded-md border p-2">
                    <div className="flex items-center gap-1">
                      <CalendarClock className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <span className={cn('min-w-0 flex-1 truncate font-medium', !s.enabled && 'text-muted-foreground line-through')}>{s.name}</span>
                      <Tooltip content="Run it now">
                        <button type="button" className={action} aria-label={`Run ${s.name} now`} disabled={act.isPending || s.lastStatus === 'running'} onClick={() => act.mutate({ run: s.name })}>
                          <Play />
                        </button>
                      </Tooltip>
                      <Tooltip content={s.enabled ? 'Turn it off' : 'Turn it on'}>
                        <button type="button" className={action} aria-label={s.enabled ? `Turn ${s.name} off` : `Turn ${s.name} on`} aria-pressed={s.enabled} onClick={() => act.mutate(s.enabled ? { off: s.name } : { on: s.name })}>
                          <Power />
                        </button>
                      </Tooltip>
                      <Tooltip content="Remove it">
                        <button type="button" className={action} aria-label={`Remove ${s.name}`} onClick={() => act.mutate({ remove: s.name })}>
                          <Trash2 />
                        </button>
                      </Tooltip>
                    </div>
                    <span className="text-muted-foreground">
                      {s.when}, mode {s.mode}
                      {s.enabled && ` · next ${when(s.next)}`}
                    </span>
                    <span className="line-clamp-2">{s.prompt}</span>
                    {s.lastAt && (
                      <span className={cn('text-muted-foreground', s.lastStatus?.startsWith('failed') && 'text-destructive-ink')}>
                        Last {when(s.lastAt)}: {s.lastStatus}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
              {data.schedules.length > 0 && <p className="text-muted-foreground">They run while this IDE is open; with it closed once code-arena schedule install has set up an OS timer.</p>}
            </section>
            <section aria-label="Agents in Arena" className="grid gap-1.5">
              <h3 className={heading}>Agents in Arena</h3>
              {data.agentsProblem ? (
                <p className="text-muted-foreground">{data.agentsProblem}</p>
              ) : (data.agents ?? []).length === 0 ? (
                <p className="text-muted-foreground">You have no agents yet: make them in Arena, under Agent Studio.</p>
              ) : (
                <ul className="grid gap-1">
                  {(data.agents ?? []).map((a) => (
                    <AgentRow key={a.id} agent={a} />
                  ))}
                </ul>
              )}
            </section>
            <section aria-label="Scheduled tasks in Arena" className="grid gap-1.5">
              <h3 className={heading}>Scheduled tasks in Arena</h3>
              {data.tasksProblem ? (
                <p className="text-muted-foreground">{data.tasksProblem}</p>
              ) : (data.tasks ?? []).length === 0 ? (
                <p className="text-muted-foreground">None yet: Arena's Scheduled tasks makes them.</p>
              ) : (
                <ul className="grid gap-1">
                  {(data.tasks ?? []).map((t) => (
                    <li key={t.id} aria-label={t.name} className="flex items-center gap-1 rounded-md border p-2">
                      <div className="grid min-w-0 flex-1">
                        <span className={cn('truncate font-medium', !t.enabled && 'text-muted-foreground line-through')}>{t.name}</span>
                        <span className="text-muted-foreground">
                          {t.cron}
                          {t.running ? ' · running' : t.lastRun ? ` · last ${when(t.lastRun.startedAt)}: ${t.lastRun.status}` : ''}
                        </span>
                      </div>
                      <Tooltip content="Run it now in Arena">
                        <button type="button" className={action} aria-label={`Run ${t.name} now in Arena`} disabled={act.isPending || t.running} onClick={() => act.mutate({ task: t.id })}>
                          <Play />
                        </button>
                      </Tooltip>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </>
        )}
      </div>
    </div>
  )
}

const everyChoices = ['30m', '1h', '2h', '6h', '12h', '1d', '7d']

function NewSchedule({ folder, saving, onAdd, onCancel }: { folder: string; saving: boolean; onAdd: (s: Record<string, string>) => void; onCancel: () => void }) {
  const [s, setS] = useState({ name: '', every: '1d', at: '09:00', mode: 'plan', prompt: '', folder })
  const daily = s.every.endsWith('d')
  const field = 'h-7 rounded border bg-background px-2 text-xs'
  return (
    <form
      aria-label="New schedule"
      className="grid gap-1.5 rounded-md border p-2"
      onSubmit={(e) => {
        e.preventDefault()
        onAdd({ ...s, at: daily ? s.at : '' })
      }}
    >
      <input className={field} value={s.name} onChange={(e) => setS({ ...s, name: e.target.value })} placeholder="Name: nightly tests" aria-label="Name" required />
      <div className="flex gap-1.5">
        <select className={cn(field, 'flex-1')} value={s.every} onChange={(e) => setS({ ...s, every: e.target.value })} aria-label="How often">
          {everyChoices.map((c) => (
            <option key={c} value={c}>
              every {c}
            </option>
          ))}
        </select>
        {daily && <input type="time" className={field} value={s.at} onChange={(e) => setS({ ...s, at: e.target.value })} aria-label="At" />}
        <select className={field} value={s.mode} onChange={(e) => setS({ ...s, mode: e.target.value })} aria-label="Mode">
          <option value="plan">reads only</option>
          <option value="auto-edit">may edit</option>
          <option value="yolo">anything</option>
        </select>
      </div>
      <input className={field} value={s.folder} onChange={(e) => setS({ ...s, folder: e.target.value })} aria-label="Folder" />
      <textarea className="min-h-16 rounded border bg-background px-2 py-1 text-xs" value={s.prompt} onChange={(e) => setS({ ...s, prompt: e.target.value })} placeholder="What to do: run the tests and fix what fails" aria-label="What to do" required />
      <div className="flex justify-end gap-1.5">
        <button type="button" className="h-7 rounded px-2 hover:bg-accent" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" disabled={saving || !s.name.trim() || !s.prompt.trim()} className="h-7 rounded bg-primary px-2 text-primary-foreground disabled:opacity-50">
          Add
        </button>
      </div>
    </form>
  )
}

/** An agent of Arena's: run with a text, and followed until it ends, what each step gave shown. */
function AgentRow({ agent: a }: { agent: ArenaAgent }) {
  const [text, setText] = useState('')
  const [open, setOpen] = useState(false)
  const [run, setRun] = useState<string | null>(null)
  const start = useMutation({
    mutationFn: () => api<{ run: string }>('/api/automation', { body: { agent: a.id, text } }),
    onSuccess: (r) => setRun(r.run),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const detail = useQuery({
    queryKey: ['code', 'agent-run', run],
    queryFn: ({ signal }) => api<AgentRun>(`/api/automation/agent-run?id=${encodeURIComponent(run!)}`, { signal }),
    enabled: !!run,
    refetchInterval: (q) => (q.state.data && ['done', 'failed', 'stopped', 'declined'].includes(q.state.data.status) ? false : 2000),
  })
  return (
    <li aria-label={a.name} className="grid gap-1 rounded-md border p-2">
      <div className="flex items-center gap-1">
        <Bot className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        <span className={cn('min-w-0 flex-1 truncate font-medium', !a.enabled && 'text-muted-foreground')}>{a.name}</span>
        {!a.mine && a.owner && <span className="text-muted-foreground">{a.owner}'s</span>}
        <Tooltip content="Run it">
          <button type="button" className={action} aria-label={`Run ${a.name}`} aria-expanded={open} onClick={() => setOpen(!open)}>
            <Play />
          </button>
        </Tooltip>
      </div>
      {a.description && <span className="line-clamp-2 text-muted-foreground">{a.description}</span>}
      {open && (
        <form
          className="flex gap-1"
          onSubmit={(e) => {
            e.preventDefault()
            start.mutate()
          }}
        >
          <input className="h-7 min-w-0 flex-1 rounded border bg-background px-2" value={text} onChange={(e) => setText(e.target.value)} placeholder="What it is given" aria-label={`What ${a.name} is given`} />
          <button type="submit" disabled={start.isPending} className="h-7 rounded bg-primary px-2 text-primary-foreground disabled:opacity-50">
            Run
          </button>
        </form>
      )}
      {run && (
        <output className="grid gap-0.5 rounded bg-muted/50 p-1.5">
          <span className="font-medium">{detail.data ? `${detail.data.status}${detail.data.error ? `: ${detail.data.error}` : ''}` : 'Starting…'}</span>
          {detail.data?.steps.map((s, i) => (
            <span key={i} className="line-clamp-3">
              {s.name ?? s.kind}: {s.status}
              {s.output ? ` · ${typeof s.output === 'string' ? s.output : JSON.stringify(s.output)}` : ''}
            </span>
          ))}
        </output>
      )}
    </li>
  )
}
