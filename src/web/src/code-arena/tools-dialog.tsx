import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, Search, Settings2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input, Textarea } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { api } from './http'

/** One tool's settings at a level: on or off, how it asks, its description for the model, fixed and default arguments. */
export interface ToolChoice {
  enabled?: boolean
  approval?: 'ask' | 'allow'
  description?: string
  defaults?: Record<string, unknown>
  fixed?: Record<string, unknown>
}

/** A tool of the session, as Configure Tools lists it: its own definition, its settings at each level, and how it is now. */
export interface ToolEntry {
  name: string
  group: string | null
  groupTitle: string
  server: string | null
  kind: 'read' | 'edit' | 'shell' | 'remote' | 'agent'
  origin: string | null
  description: string
  arguments: { name: string; type: string | null; description: string | null }[]
  enabled: boolean
  /** How it asks now: ask (every call), allow (never), mode (as the mode says of its kind), never (it only reads). */
  asks: 'ask' | 'allow' | 'mode' | 'never' | null
  person: ToolChoice
  here: ToolChoice
  project: ToolChoice
  /** Why it may not run unasked; null: it may. */
  allowLocked: string | null
}

export interface OwnTool {
  key: string
  name: string
  description: string
  source: 'project' | 'person'
  origin: string
  command: string
  kind: 'read' | 'edit' | 'shell'
  enabled: boolean
  file: string
}

export interface ProjectServer {
  key: string
  name: string
  command: string
  enabled: boolean
  connected: boolean
  file: string
}

export interface ToolSettingsList {
  tools: ToolEntry[]
  own: OwnTool[]
  servers: ProjectServer[]
  projectFile: string | null
  said?: string
}

const toolSettingsQuery = {
  queryKey: ['code', 'tool-settings'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ToolSettingsList>('/api/tools/settings', { signal }),
}

type Level = 'person' | 'here'

const asksLabel: Record<NonNullable<ToolEntry['asks']>, string> = {
  ask: 'Asks before each call',
  allow: 'Never asks',
  mode: 'Asks as the mode says',
  never: 'Reads only: never asks',
}

const kindLabel: Record<ToolEntry['kind'], string> = { read: 'reads', edit: 'edits files', shell: 'runs commands', remote: 'remote', agent: 'sub-agent' }

/**
 * Configure Tools (as VS Code's chat and the web chat's tool customisation): every tool of the session, by where it comes
 * from, each turned on or off, how it asks, its description for the model and its arguments fixed or given a default;
 * kept for you in every project, or for you in this project only. The project's own tools and MCP servers wait here to be
 * enabled. The project's settings (.arena/settings.json) may only turn a tool off or make it ask; Arena's tools are as
 * Arena's admins set them.
 */
export function ToolsDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Configure Tools</DialogTitle>
          <DialogDescription>Which tools the agent may call, how each asks you first, and what the model is told of it.</DialogDescription>
        </DialogHeader>
        {open && <ToolsEditor />}
      </DialogContent>
    </Dialog>
  )
}

type Asks = 'default' | 'ask' | 'allow'

const asksChoices: { value: Asks; label: string }[] = [
  { value: 'default', label: 'As the mode says' },
  { value: 'ask', label: 'Always asks' },
  { value: 'allow', label: 'Never asks' },
]

/**
 * Every tool in one list by where it comes from, each with its switch and how it asks, side by side; what the model is told
 * of it and its arguments under Advanced. Kept for you in every project, or (ticked) in this project only.
 */
function ToolsEditor() {
  const queryClient = useQueryClient()
  const listed = useQuery(toolSettingsQuery)
  const [level, setLevel] = useState<Level>('person')
  const [search, setSearch] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const [folded, setFolded] = useState<Set<string>>(() => new Set())
  const set = (data: ToolSettingsList) => queryClient.setQueryData(toolSettingsQuery.queryKey, data)
  const save = useMutation({
    mutationFn: ({ name, settings }: { name: string; settings: ToolChoice }) => api<ToolSettingsList>('/api/tools/settings', { body: { name, level, settings } }),
    onSuccess: set,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const enable = useMutation({
    mutationFn: ({ key, on }: { key: string; on: boolean }) => api<ToolSettingsList>('/api/tools/enable', { body: { key, on } }),
    onSuccess: (data) => {
      set(data)
      if (data.said) toast.success(data.said)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (listed.isPending) return <p className="text-sm text-muted-foreground">Reading the tools…</p>
  if (listed.error) return <p className="text-sm text-destructive">{errorMessage(listed.error)}</p>
  const data = listed.data
  const q = search.trim().toLowerCase()
  const shown = data.tools.filter((t) => !q || t.name.toLowerCase().includes(q) || t.description.toLowerCase().includes(q) || t.groupTitle.toLowerCase().includes(q))
  const groups = [...new Set(shown.map((t) => t.groupTitle))]
  const waiting = [...data.own.filter((t) => t.source === 'project'), ...data.servers]
  const at = (t: ToolEntry) => (level === 'person' ? t.person : t.here)
  return (
    <div className="grid min-h-0 gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative min-w-0 flex-1">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input className="h-8 ps-8 text-sm" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Find a tool" aria-label="Find a tool" />
        </div>
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <Switch checked={level === 'here'} onCheckedChange={(here) => setLevel(here ? 'here' : 'person')} aria-label="Only in this project" />
          Only in this project
        </label>
      </div>
      <div className="grid max-h-[60vh] gap-3 overflow-y-auto pe-1">
        {waiting.length > 0 && (
          <section aria-label="The project’s own" className="grid gap-2 rounded-md border border-dashed p-2.5">
            <h3 className="text-sm font-semibold">The project’s own, waiting for you</h3>
            <p className="text-xs text-muted-foreground">Defined in the repository: each runs only once you enable it, as it is now. A change to its command waits for you again.</p>
            <ul className="grid gap-2">
              {waiting.map((w) => (
                <li key={w.key} className="flex items-start gap-3 text-sm">
                  <div className="grid min-w-0 flex-1 gap-0.5">
                    <span className="font-mono">{w.name}</span>
                    {'description' in w && <span className="text-xs text-muted-foreground">{w.description}</span>}
                    <code className="text-xs [overflow-wrap:anywhere]">{w.command}</code>
                    <span className="text-xs text-muted-foreground">
                      {'kind' in w ? `${w.file} · ${kindLabel[w.kind]}` : `${w.file} · MCP server, each call asks${w.enabled && !w.connected ? ' · connects from the next session' : ''}`}
                    </span>
                  </div>
                  <Switch checked={w.enabled} aria-label={`Enable ${w.name} for this project`} disabled={enable.isPending} onCheckedChange={(on) => enable.mutate({ key: w.key, on })} />
                </li>
              ))}
            </ul>
          </section>
        )}
        {groups.map((g) => {
          const tools = shown.filter((t) => t.groupTitle === g)
          const isFolded = !q && folded.has(g)
          return (
            <section key={g} aria-label={g} className="grid gap-1">
              <button
                type="button"
                className="flex items-center gap-1.5 rounded text-left text-sm font-semibold hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                aria-expanded={!isFolded}
                onClick={() => setFolded((f) => (f.has(g) ? new Set([...f].filter((x) => x !== g)) : new Set([...f, g])))}
              >
                {isFolded ? <ChevronRight className="size-4" aria-hidden="true" /> : <ChevronDown className="size-4" aria-hidden="true" />}
                {g}
                <span className="text-xs font-normal text-muted-foreground tabular-nums">
                  {tools.filter((t) => t.enabled).length} of {tools.length} on
                </span>
              </button>
              {!isFolded && (
                <ul className="divide-y rounded-md border">
                  {tools.map((t) => {
                    const offByProject = t.project.enabled === false
                    const expanded = open === t.name
                    const choice = at(t)
                    const asks: Asks = choice.approval ?? 'default'
                    return (
                      <li key={t.name} aria-label={t.name} className="grid gap-2 px-2.5 py-2 text-sm">
                        <div className="flex items-center gap-2">
                          <Switch
                            checked={t.enabled}
                            disabled={offByProject || save.isPending}
                            aria-label={`${t.name} on`}
                            onCheckedChange={(on) => save.mutate({ name: t.name, settings: { ...choice, enabled: on ? undefined : false } })}
                          />
                          <div className="grid min-w-0 flex-1">
                            <Tooltip content={`${kindLabel[t.kind]}${t.origin ? ` · ${t.origin}` : ''}${t.asks ? ` · ${asksLabel[t.asks]}` : ''}`}>
                              <span className="truncate font-mono">{t.name}</span>
                            </Tooltip>
                            <span className="truncate text-xs text-muted-foreground">{offByProject ? `Turned off by the project (${data.projectFile}).` : t.description}</span>
                          </div>
                          {t.kind !== 'read' && t.asks !== 'never' && (
                            <select
                              aria-label={`How ${t.name} asks`}
                              value={asks}
                              disabled={save.isPending}
                              onChange={(e) => {
                                const v = e.target.value as Asks
                                const { approval: _, ...rest } = choice
                                save.mutate({ name: t.name, settings: v === 'default' ? rest : { ...rest, approval: v } })
                              }}
                              className="h-7 shrink-0 rounded-md border bg-card px-1.5 text-xs"
                            >
                              {asksChoices.map((a) => (
                                <option key={a.value} value={a.value} disabled={a.value === 'allow' && !!t.allowLocked}>
                                  {a.label}
                                </option>
                              ))}
                            </select>
                          )}
                          <Tooltip content="Advanced: what the model is told of it, its arguments">
                            <button
                              type="button"
                              className="grid size-6 shrink-0 place-items-center rounded text-muted-foreground hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                              aria-expanded={expanded}
                              aria-label={`Advanced settings of ${t.name}`}
                              onClick={() => setOpen(expanded ? null : t.name)}
                            >
                              <Settings2 className="size-3.5" />
                            </button>
                          </Tooltip>
                        </div>
                        {t.allowLocked && asks !== 'ask' && <span className="ms-11 text-xs text-muted-foreground">{t.allowLocked}</span>}
                        {expanded && <ToolForm key={`${t.name}-${level}`} tool={t} choice={choice} saving={save.isPending} onSave={(settings) => save.mutate({ name: t.name, settings })} />}
                      </li>
                    )
                  })}
                </ul>
              )}
            </section>
          )
        })}
        {shown.length === 0 && <p className="text-sm text-muted-foreground">No tool has “{search}” in its name or description.</p>}
      </div>
    </div>
  )
}

/** An argument's value as typed, as its type takes it: a number, true or false, JSON for an object or a list; else the text. */
function parsed(text: string, type: string | null): unknown {
  const t = text.trim()
  if (type === 'number' || type === 'integer') return Number.isFinite(Number(t)) && t !== '' ? Number(t) : t
  if (type === 'boolean') return t === 'true' ? true : t === 'false' ? false : t
  if (type === 'object' || type === 'array') {
    try {
      return JSON.parse(t)
    } catch {
      return t
    }
  }
  return text
}

const shownValue = (v: unknown) => (typeof v === 'string' ? v : JSON.stringify(v))

function ToolForm({ tool: t, choice, saving, onSave }: { tool: ToolEntry; choice: ToolChoice; saving: boolean; onSave: (c: ToolChoice) => void }) {
  const [description, setDescription] = useState(choice.description ?? '')
  const [args, setArgs] = useState<Record<string, { how: 'model' | 'default' | 'fixed'; value: string }>>(() =>
    Object.fromEntries(
      t.arguments.map((a) => [
        a.name,
        choice.fixed && a.name in choice.fixed
          ? { how: 'fixed', value: shownValue(choice.fixed[a.name]) }
          : choice.defaults && a.name in choice.defaults
            ? { how: 'default', value: shownValue(choice.defaults[a.name]) }
            : { how: 'model', value: '' },
      ]),
    ),
  )
  const pick = (how: 'model' | 'default' | 'fixed') => Object.fromEntries(t.arguments.filter((a) => args[a.name]?.how === how).map((a) => [a.name, parsed(args[a.name]!.value, a.type)]))
  return (
    <form
      className="grid gap-3 rounded-md bg-muted/40 p-3"
      aria-label={`Advanced settings of ${t.name}`}
      onSubmit={(e) => {
        e.preventDefault()
        const defaults = pick('default')
        const fixed = pick('fixed')
        onSave({
          ...(choice.enabled === false ? { enabled: false } : {}),
          ...(choice.approval ? { approval: choice.approval } : {}),
          ...(description.trim() ? { description: description.trim() } : {}),
          ...(Object.keys(defaults).length ? { defaults } : {}),
          ...(Object.keys(fixed).length ? { fixed } : {}),
        })
      }}
    >
      {t.project.approval === 'ask' && <p className="text-xs text-muted-foreground">The project makes it ask before each call, whatever is chosen here.</p>}
      <div className="grid gap-1 text-xs font-medium">
        <label htmlFor={`description-${t.name}`}>What the model is told of it</label>
        <Textarea id={`description-${t.name}`} value={description} onChange={(e) => setDescription(e.target.value)} placeholder={t.description} rows={3} className="text-sm font-normal" />
        <span className="font-normal text-muted-foreground">Empty: its own words (shown greyed).</span>
      </div>
      {t.arguments.length > 0 && (
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-xs font-medium">Its arguments</legend>
          {t.arguments.map((a) => {
            const v = args[a.name] ?? { how: 'model', value: '' }
            return (
              <div key={a.name} className="grid gap-1 sm:grid-cols-[10rem_9rem_minmax(0,1fr)] sm:items-center">
                <span className="truncate font-mono text-xs" title={a.description ?? undefined}>
                  {a.name}
                  {a.type && <span className="text-muted-foreground"> · {a.type}</span>}
                </span>
                <select
                  aria-label={`How ${a.name} is given`}
                  value={v.how}
                  onChange={(e) => setArgs((x) => ({ ...x, [a.name]: { ...v, how: e.target.value as 'model' | 'default' | 'fixed' } }))}
                  className="h-8 rounded-md border bg-card px-2 text-xs"
                >
                  <option value="model">The model gives it</option>
                  <option value="default">A default</option>
                  <option value="fixed">Always this</option>
                </select>
                {v.how !== 'model' && (
                  <Input aria-label={`The value of ${a.name}`} value={v.value} onChange={(e) => setArgs((x) => ({ ...x, [a.name]: { ...v, value: e.target.value } }))} className="h-8 font-mono text-xs" />
                )}
              </div>
            )
          })}
          <p className="text-xs text-muted-foreground">A fixed argument is not offered to the model; a default fills one it leaves out.</p>
        </fieldset>
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" loading={saving}>
          Save
        </Button>
        <Button type="button" size="sm" variant="ghost" disabled={saving} onClick={() => onSave({ ...(choice.enabled === false ? { enabled: false } : {}), ...(choice.approval ? { approval: choice.approval } : {}) })}>
          Reset
        </Button>
      </div>
    </form>
  )
}
