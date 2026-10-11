import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Bug, ChevronDown, ChevronRight, CirclePlay, Plus, Settings, Trash2, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { runCommand } from '../commands'
import { useEditor } from '../editor-state'
import { nameOf, saveWorkspaceState, workspaceStateQuery } from '../ide-api'
import { useKeyFor } from '../keyboard'
import { PartHelp } from '../help'
import { debugAdaptersQuery, runConfigsQuery } from './run-api'
import { useDebug, type Session, type Variable } from './session'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

/** A part of the view that folds, as VS Code's: its title, and what it holds. */
function Part({ title, actions, children, open: initially = true }: { title: string; actions?: ReactNode; children: ReactNode; open?: boolean }) {
  const [open, setOpen] = useState(initially)
  return (
    <div className="border-t first:border-t-0">
      <div className="flex h-[1.375rem] items-center pr-1.5">
        <button type="button" aria-expanded={open} onClick={() => setOpen(!open)} className="flex h-full min-w-0 flex-1 items-center gap-1 pl-1 text-left text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase outline-none focus-visible:ring-1 focus-visible:ring-ring">
          {open ? <ChevronDown className="size-3.5" aria-hidden="true" /> : <ChevronRight className="size-3.5" aria-hidden="true" />}
          {title}
        </button>
        {open && actions}
      </div>
      {open && <div className="pb-1">{children}</div>}
    </div>
  )
}

/** A variable, its children shown as it opens (asked of the adapter then). */
function VariableRow({ session, v, level, container }: { session: Session; v: Variable; level: number; container: number }) {
  const [open, setOpen] = useState(false)
  const [children, setChildren] = useState<Variable[] | null>(null)
  const [editing, setEditing] = useState(false)
  useEffect(() => {
    if (!open || children || v.variablesReference === 0) return
    void session.variables(v.variablesReference).then(setChildren, () => setChildren([]))
  }, [open, children, session, v.variablesReference])
  const set = async (value: string) => {
    setEditing(false)
    try {
      await session.setVariable(container, v.name, value)
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <li>
      <div className="group flex h-[1.375rem] min-w-0 items-center gap-1 pr-2 font-mono text-xs hover:bg-accent/70" style={{ paddingLeft: `${0.5 + level * 0.75}rem` }}>
        {v.variablesReference > 0 ? (
          <button type="button" aria-label={`${open ? 'Collapse' : 'Expand'} ${v.name}`} aria-expanded={open} onClick={() => setOpen(!open)} className="grid size-4 shrink-0 place-items-center">
            {open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
          </button>
        ) : (
          <span className="size-4 shrink-0" />
        )}
        <span className="shrink-0 text-primary-ink">{v.name}</span>
        <span className="shrink-0 text-muted-foreground">:</span>
        {editing ? (
          <input
            autoFocus
            defaultValue={v.value}
            aria-label={`New value of ${v.name}`}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void set(e.currentTarget.value)
              if (e.key === 'Escape') setEditing(false)
            }}
            onBlur={() => setEditing(false)}
            className="min-w-0 flex-1 rounded-sm border bg-background px-1"
          />
        ) : (
          <span className="min-w-0 truncate" title={v.type ? `${v.type}: ${v.value}` : v.value} onDoubleClick={() => session.capabilities.supportsSetVariable && setEditing(true)}>
            {v.value}
          </span>
        )}
      </div>
      {open && children && (
        <ul>
          {children.map((c) => (
            <VariableRow key={c.name} session={session} v={c} level={level + 1} container={v.variablesReference} />
          ))}
        </ul>
      )}
    </li>
  )
}

/** The variables of the frame chosen, by scope (Locals, Arguments…); an expensive scope opens when asked. */
function Variables({ session }: { session: Session }) {
  const [scopes, setScopes] = useState<{ name: string; variablesReference: number; expensive?: boolean; vars: Variable[] | null }[]>([])
  const frameId = session.frameId
  useEffect(() => {
    let live = true
    if (frameId === null || session.state !== 'stopped') return
    void session.scopes(frameId).then(async (list) => {
      const filled = await Promise.all(list.map(async (s) => ({ ...s, vars: s.expensive ? null : await session.variables(s.variablesReference).catch(() => []) })))
      if (live) setScopes(filled)
    }, () => live && setScopes([]))
    return () => {
      live = false
    }
  }, [session, frameId, session.state])
  if (session.state !== 'stopped') return <p className="px-3 text-xs text-muted-foreground">{session.state === 'running' ? 'Running: pause it (F6) or stop at a breakpoint to see its variables.' : 'Not stopped.'}</p>
  return (
    <ul aria-label="Variables">
      {scopes.map((s) => (
        <li key={s.name}>
          <div className="px-2 text-xs font-semibold text-muted-foreground">{s.name}</div>
          <ul>
            {(s.vars ?? []).map((v) => (
              <VariableRow key={v.name} session={session} v={v} level={0} container={s.variablesReference} />
            ))}
          </ul>
          {s.vars === null && (
            <button type="button" className="px-3 text-xs text-primary-ink hover:underline" onClick={() => void session.variables(s.variablesReference).then((vars) => setScopes((all) => all.map((x) => (x.name === s.name ? { ...x, vars } : x))))}>
              Show them
            </button>
          )}
        </li>
      ))}
    </ul>
  )
}

/** Watch expressions, evaluated in the frame chosen at each stop; kept for this folder. */
function Watch() {
  const d = useDebug()
  const s = d.active
  const [values, setValues] = useState<Record<string, { result?: string; error?: string }>>({})
  const [adding, setAdding] = useState(false)
  const frameId = s?.frameId
  useEffect(() => {
    let live = true
    if (!s || s.state !== 'stopped') return
    void Promise.all(
      d.watch.map(async (expr) => {
        try {
          const r = await s.evaluate(expr, 'watch')
          return [expr, { result: r.result }] as const
        } catch (e) {
          return [expr, { error: errorMessage(e) }] as const
        }
      }),
    ).then((pairs) => live && setValues(Object.fromEntries(pairs)))
    return () => {
      live = false
    }
  }, [s, frameId, s?.state, d.watch])
  return (
    <>
      <ul aria-label="Watch expressions">
        {d.watch.map((expr, i) => (
          <li key={`${expr}:${i}`} className="group flex h-[1.375rem] min-w-0 items-center gap-1 pr-1.5 pl-3 font-mono text-xs hover:bg-accent/70">
            <span className="shrink-0 text-primary-ink">{expr}</span>
            <span className="text-muted-foreground">:</span>
            <span className={cn('min-w-0 flex-1 truncate', values[expr]?.error && 'text-muted-foreground')}>{s?.state === 'stopped' ? (values[expr]?.result ?? values[expr]?.error ?? '…') : 'not available'}</span>
            <button type="button" aria-label={`Remove the watch ${expr}`} onClick={() => d.setWatch(d.watch.filter((_, j) => j !== i))} className={cn(action, 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100')}>
              <X />
            </button>
          </li>
        ))}
      </ul>
      {adding ? (
        <input
          autoFocus
          aria-label="Expression to watch"
          placeholder="An expression to watch"
          onKeyDown={(e) => {
            if (e.key === 'Enter' && e.currentTarget.value.trim()) {
              d.setWatch([...d.watch, e.currentTarget.value.trim()])
              setAdding(false)
            }
            if (e.key === 'Escape') setAdding(false)
          }}
          onBlur={() => setAdding(false)}
          className="mx-3 my-0.5 h-6 w-[calc(100%-1.5rem)] rounded-sm border bg-background px-1 font-mono text-xs"
        />
      ) : (
        <button type="button" onClick={() => setAdding(true)} className="px-3 text-xs text-muted-foreground hover:text-foreground">
          + Add expression
        </button>
      )}
    </>
  )
}

/** The threads and the frames of the one stopped; a frame opens its file at its line, and is the one variables are of. */
function CallStack({ session }: { session: Session }) {
  const ed = useEditor()
  if (session.state !== 'stopped') return <p className="px-3 text-xs text-muted-foreground">{session.state === 'running' ? 'Running' : session.state === 'starting' ? 'Starting…' : 'Ended'}</p>
  return (
    <ul aria-label="Call stack">
      {session.threads.map((t) => (
        <li key={t.id}>
          <div className="px-2 text-xs font-semibold text-muted-foreground">
            {t.name} {t.id === session.stopped?.threadId && <span className="font-normal">· paused on {session.stopped?.reason}</span>}
          </div>
          {t.id === (session.stopped?.threadId ?? session.threads[0]?.id) && (
            <ul>
              {session.frames.map((f) => (
                <li key={f.id}>
                  <button
                    type="button"
                    onClick={() => {
                      session.selectFrame(f.id)
                      if (f.path) void ed.open(f.path, { line: f.line, column: f.column })
                    }}
                    className={cn('flex h-[1.375rem] w-full min-w-0 items-center gap-2 pr-2 pl-4 text-left text-xs outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring', f.id === session.frameId && 'bg-accent', !f.path && 'text-muted-foreground')}
                  >
                    <span className="min-w-0 truncate font-mono">{f.name}</span>
                    <span className="ml-auto shrink-0 text-muted-foreground">{f.path ? `${nameOf(f.path)}:${f.line}` : (f.source?.name ?? '')}</span>
                  </button>
                </li>
              ))}
              {session.frames.length >= 20 && session.frames.length % 20 === 0 && (
                <li>
                  <button type="button" className="px-4 text-xs text-primary-ink hover:underline" onClick={() => void session.loadFrames(session.stopped?.threadId ?? session.threads[0]!.id, session.frames.length)}>
                    Load more frames
                  </button>
                </li>
              )}
            </ul>
          )}
        </li>
      ))}
    </ul>
  )
}

/** Every breakpoint, by file: on or off, its condition, removed; and the exceptions to stop at. */
function Breakpoints() {
  const d = useDebug()
  const ed = useEditor()
  const s = d.active
  const filters = (s?.capabilities.exceptionBreakpointFilters as { filter: string; label: string; default?: boolean }[] | undefined) ?? []
  const chosen = d.exceptionFilters ?? filters.filter((f) => f.default).map((f) => f.filter)
  const all = Object.entries(d.breakpoints).flatMap(([path, list]) => list.map((b) => ({ path, b })))
  return (
    <>
      {filters.length > 0 && (
        <ul aria-label="Exception breakpoints">
          {filters.map((f) => (
            <li key={f.filter} className="flex h-[1.375rem] items-center gap-1.5 pl-3 text-xs">
              <input type="checkbox" id={`exc-${f.filter}`} checked={chosen.includes(f.filter)} onChange={(e) => d.setExceptionFilters(e.target.checked ? [...chosen, f.filter] : chosen.filter((x) => x !== f.filter))} className="accent-primary" />
              <label htmlFor={`exc-${f.filter}`}>{f.label}</label>
            </li>
          ))}
        </ul>
      )}
      <ul aria-label="Breakpoints">
        {all.map(({ path, b }) => (
          <li key={`${path}:${b.line}`} className="group flex h-[1.375rem] min-w-0 items-center gap-1.5 pr-1.5 pl-3 text-xs hover:bg-accent/70">
            <input
              type="checkbox"
              aria-label={`Breakpoint at ${path}:${b.line} on`}
              checked={b.enabled}
              onChange={(e) => d.setBreakpoints(path, (d.breakpoints[path] ?? []).map((x) => (x.line === b.line ? { ...x, enabled: e.target.checked } : x)))}
              className="accent-primary"
            />
            <button type="button" className="flex min-w-0 flex-1 items-center gap-1.5 text-left" onClick={() => void ed.open(path, { line: b.line })}>
              <span className="truncate">{nameOf(path)}</span>
              <span className="text-muted-foreground tabular-nums">{b.line}</span>
              {(b.condition || b.hitCondition || b.logMessage) && <span className="min-w-0 truncate font-mono text-muted-foreground">{b.logMessage ? `log ${b.logMessage}` : (b.condition ?? `hit ${b.hitCondition}`)}</span>}
            </button>
            <button type="button" aria-label={`Remove the breakpoint at ${path}:${b.line}`} onClick={() => d.setBreakpoints(path, (d.breakpoints[path] ?? []).filter((x) => x.line !== b.line))} className={cn(action, 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100')}>
              <X />
            </button>
          </li>
        ))}
        {all.length === 0 && <li className="px-3 text-xs text-muted-foreground">None: click beside a line number, or press F9.</li>}
      </ul>
    </>
  )
}

/**
 * Run and Debug (Ctrl+Shift+D), as VS Code's: a configuration to start (launch.json's, or one the project's files
 * give), Start Debugging (F5) and Run Without Debugging (Ctrl+F5), Debug or Release; while a session runs, its variables,
 * watch expressions and call stack; and the breakpoints.
 */
export function RunDebugView({ onStart }: { onStart: (name: string, noDebug: boolean) => void }) {
  const d = useDebug()
  const queryClient = useQueryClient()
  const configs = useQuery(runConfigsQuery)
  const adapters = useQuery({ ...debugAdaptersQuery, enabled: !!configs.data })
  const state = useQuery(workspaceStateQuery)
  const startKey = useKeyFor('workbench.action.debug.start')
  const runKey = useKeyFor('workbench.action.debug.run')
  const shown = (configs.data?.configurations ?? []).filter((c) => !c.hidden)
  const saved = typeof state.data?.['launch.selected'] === 'string' ? (state.data['launch.selected'] as string) : undefined
  const [chosen, setChosen] = useState<string | undefined>(saved)
  const current = shown.find((c) => c.name === (chosen ?? saved)) ?? shown[0]
  const s = d.active
  const missing = current && adapters.data && !adapters.data.some((a) => a.found && a.types.includes(current.type)) ? adapters.data.find((a) => a.types.includes(current.type)) : undefined
  const choose = (name: string) => {
    setChosen(name)
    void saveWorkspaceState({ 'launch.selected': name }).catch(() => undefined)
  }
  const build = configs.data?.buildConfiguration ?? 'Debug'
  return (
    <div className="flex h-full min-h-0 flex-col" aria-label="Run and Debug">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Run and Debug</h2>
        <Tooltip content="Open launch.json">
          <button type="button" aria-label="Open launch.json" onClick={() => runCommand('workbench.action.debug.configure')} className={action}>
            <Settings />
          </button>
        </Tooltip>
        <PartHelp part="debug" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        <div className="grid gap-1.5 px-3 pb-2">
          {configs.isPending && <p className="text-xs text-muted-foreground">Looking for run configurations…</p>}
          {configs.error && <p className="text-xs text-muted-foreground">{errorMessage(configs.error)}</p>}
          {configs.data && shown.length === 0 && <p className="text-xs text-muted-foreground">No run configuration: add one to .vscode/launch.json.</p>}
          {shown.length > 0 && (
            <select aria-label="Run configuration" value={current?.name} onChange={(e) => choose(e.target.value)} className="h-7 rounded-md border bg-background px-1.5 text-xs">
              {(['launch', 'detected'] as const).map((src) =>
                shown.some((c) => c.source === src) ? (
                  <optgroup key={src} label={src === 'launch' ? 'launch.json' : 'Detected'}>
                    {shown
                      .filter((c) => c.source === src)
                      .map((c) => (
                        <option key={c.name} value={c.name}>
                          {c.name}
                        </option>
                      ))}
                  </optgroup>
                ) : null,
              )}
            </select>
          )}
          {current && (
            <div className="flex gap-1.5">
              <Button size="sm" className="h-7 flex-1 text-xs" onClick={() => onStart(current.name, false)} title={startKey ? `Start Debugging (${startKey})` : 'Start Debugging'}>
                <Bug /> Start Debugging
              </Button>
              <Button size="sm" variant="outline" className="h-7 text-xs" disabled={!current.supported} onClick={() => onStart(current.name, true)} title={current.supported ? (runKey ? `Run Without Debugging (${runKey})` : 'Run Without Debugging') : (current.reason ?? '')}>
                <CirclePlay /> Run
              </Button>
            </div>
          )}
          {current?.command && <p className="truncate font-mono text-[0.6875rem] text-muted-foreground" title={current.command}>{current.command}</p>}
          {missing && (
            <Alert variant="warning" title={`${missing.name} is not here`}>
              {missing.reason ?? 'Debugging this needs it.'} {missing.install && <span className="font-mono">{missing.install}</span>}
            </Alert>
          )}
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            Build
            <select
              aria-label="Build configuration"
              value={build}
              onChange={(e) => void saveWorkspaceState({ buildConfiguration: e.target.value }).then(() => queryClient.invalidateQueries({ queryKey: ['code', 'run'] }), (err: unknown) => toast.error(errorMessage(err)))}
              className="h-6 rounded-md border bg-background px-1 text-xs text-foreground"
            >
              <option value="Debug">Debug</option>
              <option value="Release">Release</option>
            </select>
            <button type="button" className="ml-auto text-primary-ink hover:underline" onClick={() => runCommand('debug.addConfiguration')}>
              <Plus className="inline size-3" /> Add Configuration…
            </button>
          </label>
          {configs.data?.errors.map((e) => (
            <p key={`${e.file}:${e.line}`} className="text-xs text-destructive-ink">
              {e.file}:{e.line} {e.message}
            </p>
          ))}
        </div>
        {d.sessions.length > 1 && (
          <Part title="Sessions">
            <ul>
              {d.sessions.map((x) => (
                <li key={x.id}>
                  <button type="button" onClick={() => d.select(x.id)} className={cn('flex h-[1.375rem] w-full items-center gap-2 px-3 text-left text-xs hover:bg-accent/70', x === s && 'bg-accent')}>
                    {x.name} <span className="ml-auto text-muted-foreground">{x.state}</span>
                  </button>
                </li>
              ))}
            </ul>
          </Part>
        )}
        {s && (
          <>
            {s.error && (
              <Alert variant="warning" title="The debugger" className="mx-3 mb-2">
                {s.error}
              </Alert>
            )}
            {s.exception && (
              <Alert variant="destructive" title={`Exception: ${s.exception.id ?? ''}`} className="mx-3 mb-2">
                {s.exception.description}
              </Alert>
            )}
            <Part title="Variables">
              <Variables session={s} />
            </Part>
          </>
        )}
        <Part title="Watch">
          <Watch />
        </Part>
        {s && (
          <Part title="Call Stack">
            <CallStack session={s} />
          </Part>
        )}
        <Part
          title="Breakpoints"
          actions={
            <Tooltip content="Remove all breakpoints">
              <button type="button" aria-label="Remove all breakpoints" onClick={() => d.removeAll()} className={action}>
                <Trash2 />
              </button>
            </Tooltip>
          }
        >
          <Breakpoints />
        </Part>
      </div>
    </div>
  )
}
