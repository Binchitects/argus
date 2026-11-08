import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CalendarClock, CircleCheck, CircleX, KeyRound, Loader2, Pencil, Play, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Secret } from '@/components/app/secret'
import { TimeZonePicker } from '@/components/app/time-zone-picker'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { configQuery } from './chat/api'
import { blankSchedule, cronOf, describe, scheduleOf, weekdays, type Repeat, type Schedule } from './tasks-schedule'
import { gitlabEvents as studioGitlabEvents } from './studio/studio-api'

interface Run {
  id: string
  startedAt: string
  finishedAt: string | null
  status: 'running' | 'done' | 'failed' | 'skipped'
  conversationId: string | null
  /** The review it started (an app path); null: it asked in a chat. */
  link?: string | null
  error: string | null
  manual: boolean
  delivery: string | null
}

interface Task {
  id: string
  name: string
  prompt: string
  cron: string
  timeZone: string
  model: string | null
  thinking: string | null
  tools: string[] | null
  sameChat: boolean
  email: boolean
  webhookSet: boolean
  enabled: boolean
  nextRunAt: string | null
  running: boolean
  nextRuns: string[]
  lastRun: Run | null
  trigger: Trigger
  events: string[]
  replyInGitLab: boolean
  /** Where events go, for a task they run. */
  hookUrl: string | null
  /** Only in the answer that made it. */
  hookToken?: string | null
  action: Action
  repositories: string[]
  ref: string | null
  cross: boolean
}

type Action = 'ask' | 'review-repository' | 'review-repositories' | 'security' | 'security-several'

/** What a task does, as its form offers it: one repository or several for each review. */
const actions: { value: Action; label: string; several?: boolean; hint: string }[] = [
  { value: 'ask', label: 'Ask a question', hint: 'Its question in a chat, with your model and tools.' },
  { value: 'review-repository', label: 'Review a whole repository', hint: 'Its product’s code read file by file, then a summary of its health and a verdict.' },
  { value: 'review-repositories', label: 'Review several repositories', several: true, hint: 'Each reviewed whole, then looked at across: their links, boundaries and what to fix first.' },
  { value: 'security', label: 'Security review of a repository', hint: 'Its weaknesses, vulnerable packages and secrets, with what is new since the last review.' },
  { value: 'security-several', label: 'Security review of several', several: true, hint: 'Each repository’s security, then what they share and the paths from one to another.' },
]

type Trigger = 'schedule' | 'webhook' | 'gitlab'

const gitlabEvents: { value: string; label: string }[] = studioGitlabEvents.map(([value, label]) => ({ value, label }))

/** "On a schedule" in words, or what runs it. */
function runsOn(t: Task) {
  if (t.trigger === 'webhook') return 'On a webhook'
  if (t.trigger === 'gitlab') return `On GitLab: ${t.events.map((e) => gitlabEvents.find((g) => g.value === e)?.label.toLowerCase() ?? e).join(', ')}`
  return `${describe(t.cron)} · ${t.timeZone}`
}

/** A scheduled task a plugin offers (or one of the ready-made ones for developers), to add to one's own. */
interface TaskTemplate {
  id: string
  name: string
  description: string | null
  prompt: string
  cron: string
  tools: string[]
  plugin: string
  pluginTitle: string | null
  action?: Action
}

/** Ready-made tasks for developers and code review: the repositories are the person's to fill in. */
const forDevelopers: TaskTemplate[] = [
  {
    id: 'dev-mrs', name: 'Merge requests waiting', description: 'Each morning: the open merge requests of your projects, the oldest and those waiting on you first.', cron: '0 9 * * 1-5', tools: [], plugin: '', pluginTitle: null,
    prompt: 'List the open merge requests in my projects (use mr_list for each project I work on). For each: title, author, age, whether CI passes, and whether it waits on a review. Put those older than three days and those waiting on me first, then a one-line summary.',
  },
  {
    id: 'dev-ci', name: 'Failed pipelines', description: 'Each morning: the pipelines that failed since yesterday, each with the job and the lines that say why.', cron: '30 8 * * 1-5', tools: [], plugin: '', pluginTitle: null,
    prompt: 'Find the pipelines in my projects that failed since yesterday (ci_status). For each: the project, the branch or merge request, the job that failed, and from its log (job_log) the few lines that say why. Group the same cause together and say which look flaky.',
  },
  {
    id: 'dev-release', name: 'Release notes', description: 'Each Friday: what was merged this week, written as release notes.', cron: '0 15 * * 5', tools: [], plugin: '', pluginTitle: null,
    prompt: 'Write release notes for what was merged this week in my projects (mr_list with state merged since Monday). Group them as New, Improved and Fixed, one line each in plain words for users, with the merge request number; leave out internal changes.',
  },
  {
    id: 'dev-repo-review', name: 'Weekly repository review', description: 'Each Monday: a whole repository reviewed, its health and what to fix first.', cron: '0 6 * * 1', tools: [], plugin: '', pluginTitle: null, action: 'review-repository', prompt: '',
  },
  {
    id: 'dev-product-review', name: 'Weekly product review', description: 'Each Monday: several repositories reviewed, then looked at across.', cron: '0 5 * * 1', tools: [], plugin: '', pluginTitle: null, action: 'review-repositories', prompt: '',
  },
  {
    id: 'dev-security', name: 'Nightly security review', description: 'Each night: a repository’s security, with what is new since the night before.', cron: '0 2 * * *', tools: [], plugin: '', pluginTitle: null, action: 'security', prompt: '',
  },
  {
    id: 'dev-security-several', name: 'Weekly security review of a product', description: 'Each Monday: several repositories’ security, what they share and what to fix first.', cron: '0 4 * * 1', tools: [], plugin: '', pluginTitle: null, action: 'security-several', prompt: '',
  },
]

interface TasksView {
  enabled: boolean
  perPerson: number
  minIntervalMinutes: number
  webhookHosts: string
  /** Where email goes, when the app can send it. */
  email: string | null
  tasks: Task[]
  /** Whether tasks can read GitLab and comment there (Settings → Scheduled tasks → GitLab bot token). */
  gitlabBot: boolean
}

const when = (at: string, timeZone?: string) =>
  new Date(at).toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', timeZone })

/** Questions asked on a schedule, as you: a morning digest, a weekly report. Each run is a chat, and a notification. */
export function TasksPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const tasks = useQuery({
    queryKey: ['tasks'],
    queryFn: ({ signal }) => api<TasksView>('/api/tasks', { signal }),
    refetchInterval: (q) => (q.state.data?.tasks.some((t) => t.running) ? 3000 : 30_000),
  })
  const [editing, setEditing] = useState<Task | 'new' | null>(null)
  const [starting, setStarting] = useState<TaskTemplate | null>(null)
  const templates = useQuery({ queryKey: ['tasks', 'templates'], queryFn: ({ signal }) => api<TaskTemplate[]>('/api/tasks/templates', { signal }) })
  const [connect, setConnect] = useState<{ name: string; url: string; secret: string; gitlab: boolean } | null>(null)
  const rotate = useMutation({
    mutationFn: (t: Task) => api<{ hookToken: string }>(`/api/tasks/${t.id}/secret`, { method: 'POST' }),
    onSuccess: (r, t) => setConnect({ name: t.name, url: t.hookUrl ?? '', secret: r.hookToken, gitlab: t.trigger === 'gitlab' }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const changed = () => queryClient.invalidateQueries({ queryKey: ['tasks'] })
  const run = useMutation({
    mutationFn: (t: Task) => api(`/api/tasks/${t.id}/run`, { method: 'POST' }),
    onSuccess: async (_, t) => {
      await changed()
      toast.success(`${t.name} is running`, { description: 'Its answer lands as a chat, and under the bell.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const toggle = useMutation({
    mutationFn: (t: Task) => api(`/api/tasks/${t.id}`, { method: 'PATCH', body: { enabled: !t.enabled } }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (t: Task) => api(`/api/tasks/${t.id}`, { method: 'DELETE' }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (tasks.isPending) return <PageSkeleton />
  if (tasks.error) return <QueryError error={tasks.error} retry={() => tasks.refetch()} />
  const view = tasks.data
  return (
    <>
      <PageHeader
        title="Scheduled tasks"
        description="Questions asked on a schedule, as you, with your model and tools: a morning digest, a weekly report on a repository. Each run is a chat, and a notification; an email or a post to your team's channel if you like."
        actions={
          <Button onClick={() => setEditing('new')} disabled={!view.enabled || view.tasks.length >= view.perPerson}>
            <Plus /> New task
          </Button>
        }
      />
      {!view.enabled && <Alert className="mb-4">Scheduled tasks are off here. An admin turns them on under Settings → Scheduled tasks.</Alert>}
      {view.tasks.length === 0 ? (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-10 text-center">
            <CalendarClock className="size-8 text-muted-foreground" aria-hidden="true" />
            <p className="max-w-md text-sm text-muted-foreground">
              No tasks yet. Ask for the same thing every morning, every Monday or every few hours, and read the answer when it suits you.
            </p>
            {view.enabled && (
              <Button variant="outline" onClick={() => setEditing('new')}>
                <Plus /> New task
              </Button>
            )}
          </CardContent>
        </Card>
      ) : (
        <div className="grid gap-4 xl:grid-cols-2">
          {view.tasks.map((t) => (
            <Card key={t.id} aria-label={t.name}>
              <CardHeader className="flex flex-row flex-wrap items-start gap-3">
                <div className="grid min-w-0 flex-1 gap-1">
                  <CardTitle className="flex flex-wrap items-center gap-2 text-base [overflow-wrap:anywhere]">
                    {t.name}
                    {!t.enabled && <Badge variant="outline">Off</Badge>}
                    {t.running && (
                      <Badge>
                        <Loader2 className="animate-spin" aria-hidden="true" /> Running
                      </Badge>
                    )}
                  </CardTitle>
                  <CardDescription>
                    {runsOn(t)}
                    {t.replyInGitLab ? ' · answers in GitLab' : ''}
                    {t.model ? ` · ${t.model}` : ''}
                    {t.sameChat ? ' · one chat' : ''}
                    {t.email ? ' · email' : ''}
                    {t.webhookSet ? ' · webhook' : ''}
                  </CardDescription>
                </div>
                <Switch checked={t.enabled} disabled={toggle.isPending} onCheckedChange={() => toggle.mutate(t)} aria-label={`${t.name} on`} />
              </CardHeader>
              <CardContent className="grid gap-3 text-sm">
                {t.action === 'ask' ? (
                  <p dir="auto" className="line-clamp-3 whitespace-pre-wrap text-muted-foreground">
                    {t.prompt}
                  </p>
                ) : (
                  <p className="text-muted-foreground">
                    <span className="font-medium text-foreground">{actions.find((a) => a.value === t.action)?.label}</span>: {t.repositories.join(', ')}
                    {t.ref ? ` @ ${t.ref}` : ''}
                    {actions.find((a) => a.value === t.action)?.several && !t.cross ? ' (not looked at across)' : ''}
                  </p>
                )}
                {t.enabled && t.nextRuns.length > 0 && (
                  <p className="text-xs text-muted-foreground">
                    Next: {t.nextRuns.map((r) => when(r, t.timeZone)).join(' · ')}
                  </p>
                )}
                {t.hookUrl && <CodeBlock code={t.hookUrl} label={`${t.name} address`} />}
                {t.lastRun && <LastRun run={t.lastRun} />}
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" size="sm" onClick={() => run.mutate(t)} disabled={t.running || run.isPending}>
                    <Play /> Run now
                  </Button>
                  <Button variant="outline" size="sm" onClick={() => setEditing(t)}>
                    <Pencil /> Edit
                  </Button>
                  {t.hookUrl && (
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={async () => {
                        if (await confirm({ title: 'Make a new secret?', description: 'The current secret stops working at once: set the new one where events come from.', confirm: 'Make a new secret', destructive: true }))
                          rotate.mutate(t)
                      }}
                    >
                      <KeyRound /> New secret
                    </Button>
                  )}
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={async () => {
                      if (await confirm({ title: `Remove ${t.name}?`, description: 'Its chats stay.', confirm: 'Remove', destructive: true })) remove.mutate(t)
                    }}
                  >
                    <Trash2 /> Remove
                  </Button>
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
      {view.enabled && (
        <section className="mt-8 grid gap-3" aria-label="For developers">
          <h2 className="text-lg font-semibold">For developers</h2>
          <ul className="grid gap-2 xl:grid-cols-2">
            {forDevelopers.map((t) => (
              <li key={t.id} className="flex items-start gap-3 rounded-lg border p-3 text-sm">
                <span className="grid min-w-0 flex-1 gap-0.5">
                  <span className="font-medium">{t.name}</span>
                  <span className="text-xs text-muted-foreground">
                    {actions.find((a) => a.value === (t.action ?? 'ask'))!.label} · {describe(t.cron)}
                  </span>
                  {t.description && <span className="text-muted-foreground">{t.description}</span>}
                </span>
                <Button size="sm" variant="outline" disabled={view.tasks.length >= view.perPerson} onClick={() => setStarting(t)} aria-label={`Add ${t.name}`}>
                  <Plus /> Add
                </Button>
              </li>
            ))}
          </ul>
        </section>
      )}
      {!!templates.data?.length && view.enabled && (
        <section className="mt-8 grid gap-3" aria-label="From plugins">
          <h2 className="text-lg font-semibold">From plugins</h2>
          <ul className="grid gap-2 xl:grid-cols-2">
            {templates.data.map((t) => (
              <li key={t.id} className="flex items-start gap-3 rounded-lg border p-3 text-sm">
                <span className="grid min-w-0 flex-1 gap-0.5">
                  <span className="font-medium">{t.name}</span>
                  <span className="text-xs text-muted-foreground">
                    {t.pluginTitle ?? t.plugin} · {describe(t.cron)}
                  </span>
                  {t.description && <span className="text-muted-foreground">{t.description}</span>}
                </span>
                <Button size="sm" variant="outline" disabled={view.tasks.length >= view.perPerson} onClick={() => setStarting(t)}>
                  <Plus /> Add
                </Button>
              </li>
            ))}
          </ul>
        </section>
      )}
      <Dialog
        open={editing !== null || starting !== null}
        onOpenChange={(o) => {
          if (!o) {
            setEditing(null)
            setStarting(null)
          }
        }}
      >
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {(editing !== null || starting !== null) && (
            <TaskForm
              key={starting ? `template-${starting.id}` : editing === 'new' ? 'new' : editing!.id}
              saved={editing === 'new' || editing === null ? null : editing}
              start={starting ?? undefined}
              view={view}
              onClose={() => {
                setEditing(null)
                setStarting(null)
              }}
              onSaved={async (made) => {
                await changed()
                if (made?.hookToken && made.hookUrl) setConnect({ name: made.name, url: made.hookUrl, secret: made.hookToken, gitlab: made.trigger === 'gitlab' })
              }}
            />
          )}
        </DialogContent>
      </Dialog>
      <Dialog open={connect !== null} onOpenChange={(o) => !o && setConnect(null)}>
        <DialogContent className="sm:max-w-xl">
          {connect && (
            <>
              <DialogHeader>
                <DialogTitle>Send events to {connect.name}</DialogTitle>
                <DialogDescription>
                  {connect.gitlab
                    ? 'In GitLab: the project (or group) → Settings → Webhooks → Add new webhook, with this address and secret token, and the events the task takes.'
                    : 'Have the system post JSON to this address, with the secret in the X-Hook-Secret header.'}
                </DialogDescription>
              </DialogHeader>
              <div className="grid gap-3">
                <CodeBlock code={connect.url} label="Address" />
                <Secret label="Secret" value={connect.secret} />
                <p className="text-xs text-muted-foreground">The secret is shown this once. Lost it? Make a new one on the task's card.</p>
              </div>
              <DialogFooter>
                <Button onClick={() => setConnect(null)}>Done</Button>
              </DialogFooter>
            </>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}

function LastRun({ run }: { run: Run }) {
  const icon = run.status === 'done' ? <CircleCheck className="size-4 text-success" aria-hidden="true" /> : run.status === 'running' ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : <CircleX className="size-4 text-destructive-ink" aria-hidden="true" />
  const status = { done: 'Done', running: 'Running', failed: 'Failed', skipped: 'Skipped' }[run.status]
  return (
    <div className="grid gap-1 rounded-lg border bg-muted/30 p-2.5 text-xs">
      <p className="flex flex-wrap items-center gap-1.5">
        {icon}
        <span className="font-medium">{status}</span>
        <span className="text-muted-foreground">
          · {when(run.startedAt)}
          {run.manual ? ' · run by hand' : ''}
        </span>
        {run.conversationId && (
          <Link to={`/chat/${run.conversationId}`} className="ml-auto text-primary-ink underline underline-offset-2">
            Open the chat
          </Link>
        )}
        {run.link && (
          <Link to={run.link} className="ml-auto text-primary-ink underline underline-offset-2">
            Open the review
          </Link>
        )}
      </p>
      {run.error && <p className="text-destructive-ink">{run.error}</p>}
      {run.delivery && <p className="text-muted-foreground">{run.delivery}</p>}
    </div>
  )
}

const DEFAULT = '(default)'

function TaskForm({ saved, start, view, onClose, onSaved }: { saved: Task | null; start?: TaskTemplate; view: TasksView; onClose: () => void; onSaved: (made?: Task) => Promise<unknown> }) {
  const config = useQuery(configQuery)
  const [form, setForm] = useState({
    name: saved?.name ?? start?.name ?? '',
    prompt: saved?.prompt ?? start?.prompt ?? '',
    timeZone: saved?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone ?? 'UTC',
    model: saved?.model ?? '',
    tools: saved?.tools ?? (start?.tools.length ? start.tools : null),
    sameChat: saved?.sameChat ?? false,
    email: saved?.email ?? false,
    webhook: '',
    trigger: saved?.trigger ?? ('schedule' as Trigger),
    events: saved?.events ?? ['merge_request'],
    replyInGitLab: saved?.replyInGitLab ?? false,
    action: saved?.action ?? start?.action ?? ('ask' as Action),
    repositories: (saved?.repositories ?? []).join('\n'),
    ref: saved?.ref ?? '',
    cross: saved?.cross ?? true,
  })
  const action = actions.find((a) => a.value === form.action)!
  const [schedule, setSchedule] = useState<Schedule>(saved ? scheduleOf(saved.cron) : start ? scheduleOf(start.cron) : blankSchedule)
  const [error, setError] = useState<string | null>(null)
  const tools = config.data?.tools ?? []
  const toolsOn = form.tools ?? tools.filter((t) => t.onByDefault).map((t) => t.id)
  const save = useMutation({
    mutationFn: () => {
      const repositories = form.repositories.split(/[\n,]+/).map((x) => x.trim()).filter(Boolean)
      const body = { ...form, repositories, cron: form.trigger === 'schedule' ? cronOf(schedule) : '', webhook: form.webhook || (saved ? null : '') }
      return saved ? api<Task>(`/api/tasks/${saved.id}`, { method: 'PATCH', body }) : api<Task>('/api/tasks', { body })
    },
    onSuccess: async (made) => {
      await onSaved(made)
      toast.success(saved ? 'Task saved.' : 'Task added.', { description: 'Run it now to see what it brings.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const set = (patch: Partial<Schedule>) => setSchedule({ ...schedule, ...patch })
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'New task'}</DialogTitle>
        <DialogDescription>
          Asked as you, with your model and tools: at the times you choose (not more often than every {view.minIntervalMinutes} minutes), or when an event comes in.
        </DialogDescription>
      </DialogHeader>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          setError(null)
          save.mutate()
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <Field label="Name" hint="e.g. Morning digest.">
          <Input required maxLength={100} autoComplete="off" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </Field>
        <Field label="What it does" hint={action.hint}>
          <Select value={form.action} onValueChange={(v) => setForm({ ...form, action: v as Action })}>
            <SelectTrigger className="min-w-0">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {actions.map((a) => (
                <SelectItem key={a.value} value={a.value}>
                  {a.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        {form.action === 'ask' ? (
          <Field label="What to ask" hint="As you would write it in the chat. With Argus on, it can read your repositories.">
            <Textarea dir="auto" required maxLength={20000} className="min-h-28" value={form.prompt} onChange={(e) => setForm({ ...form, prompt: e.target.value })} />
          </Field>
        ) : (
          <>
            {action.several ? (
              <Field label="Repositories" hint="One a line, as group/project.">
                <Textarea required rows={3} value={form.repositories} onChange={(e) => setForm({ ...form, repositories: e.target.value })} placeholder={'payments/api\npayments/web'} />
              </Field>
            ) : (
              <Field label="Repository">
                <Input required value={form.repositories} onChange={(e) => setForm({ ...form, repositories: e.target.value })} placeholder="group/project" />
              </Field>
            )}
            <Field label="Branch, tag or commit" hint="Empty: each repository’s default branch.">
              <Input value={form.ref} maxLength={300} onChange={(e) => setForm({ ...form, ref: e.target.value })} placeholder="the default branch" />
            </Field>
            {action.several && (
              <div className="flex items-center gap-2 text-sm">
                <Checkbox id="task-cross" checked={form.cross} onCheckedChange={(v) => setForm({ ...form, cross: v === true })} />
                <label htmlFor="task-cross">Look across them</label>
              </div>
            )}
          </>
        )}
        <Tabs value={form.trigger} onValueChange={(v) => setForm({ ...form, trigger: v as Trigger })}>
          <TabsList aria-label="Runs">
            <TabsTrigger value="schedule">On a schedule</TabsTrigger>
            <TabsTrigger value="gitlab">On GitLab events</TabsTrigger>
            <TabsTrigger value="webhook">On a webhook</TabsTrigger>
          </TabsList>
          {/* Each way it runs, its own panel: the tabs say which panel they show. */}
          <TabsContent value="schedule">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Repeat">
                <Select value={schedule.repeat} onValueChange={(v) => set({ repeat: v as Repeat, cron: cronOf(schedule) })}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="minutes">Every few minutes</SelectItem>
                    <SelectItem value="daily">Every day</SelectItem>
                    <SelectItem value="weekdays">Weekdays</SelectItem>
                    <SelectItem value="weekly">Once a week</SelectItem>
                    <SelectItem value="monthly">Once a month</SelectItem>
                    <SelectItem value="hours">Every few hours</SelectItem>
                    <SelectItem value="custom">Custom (cron)</SelectItem>
                  </SelectContent>
                </Select>
              </Field>
              {schedule.repeat === 'custom' ? (
                <Field label="Cron" hint="Minute, hour, day of the month, month, day of the week.">
                  <Input required className="font-mono" autoComplete="off" value={schedule.cron} onChange={(e) => set({ cron: e.target.value })} />
                </Field>
              ) : schedule.repeat === 'minutes' ? (
                <Field label="Every (minutes)" hint={`At least ${view.minIntervalMinutes}.`}>
                  <Input type="number" min={Math.max(5, view.minIntervalMinutes)} max={30} required value={schedule.minutes} onChange={(e) => set({ minutes: Number(e.target.value) })} />
                </Field>
              ) : schedule.repeat === 'hours' ? (
                <Field label="Every (hours)">
                  <Input type="number" min={1} max={23} required value={schedule.hours} onChange={(e) => set({ hours: Number(e.target.value) })} />
                </Field>
              ) : (
                <Field label="At">
                  <Input type="time" required value={schedule.time} onChange={(e) => set({ time: e.target.value })} />
                </Field>
              )}
              {schedule.repeat === 'weekly' && (
                <Field label="On">
                  <Select value={String(schedule.day)} onValueChange={(v) => set({ day: Number(v) })}>
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {weekdays.map((d, i) => (
                        <SelectItem key={d} value={String(i + 1)}>
                          {d}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </Field>
              )}
              {schedule.repeat === 'monthly' && (
                <Field label="On day">
                  <Input type="number" min={1} max={28} required value={schedule.date} onChange={(e) => set({ date: Number(e.target.value) })} />
                </Field>
              )}
            </div>
          </TabsContent>
          <TabsContent value="gitlab">
            <fieldset className="grid gap-2">
              <legend className="mb-1 text-sm font-medium">Events</legend>
              {gitlabEvents.map((g) => (
                <Label key={g.value} className="font-normal">
                  <input
                    type="checkbox"
                    className="size-3.5 accent-primary"
                    checked={form.events.includes(g.value)}
                    onChange={(e) => setForm({ ...form, events: e.target.checked ? [...form.events, g.value] : form.events.filter((x) => x !== g.value) })}
                  />
                  {g.label}
                </Label>
              ))}
              <div className="mt-1 flex items-start gap-2">
                <Switch id="task-reply" checked={form.replyInGitLab} disabled={!view.gitlabBot && !form.replyInGitLab} onCheckedChange={(replyInGitLab) => setForm({ ...form, replyInGitLab })} />
                <div className="grid gap-0.5">
                  <Label htmlFor="task-reply">Answer as a comment in GitLab</Label>
                  <p className="text-xs text-muted-foreground">
                    {view.gitlabBot
                      ? 'On the merge request, issue or commit, by the GitLab bot; each comment is audited.'
                      : 'The GitLab bot is not set up (an admin sets its token under Settings → Scheduled tasks). It also reads a merge request’s changes and a failed job’s log.'}
                  </p>
                </div>
              </div>
            </fieldset>
          </TabsContent>
          <TabsContent value="webhook">
            <p className="text-sm text-muted-foreground">Any system can run it by posting JSON to its address, with its secret. What it posts is added to the question.</p>
          </TabsContent>
        </Tabs>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Time zone">
            <TimeZonePicker value={form.timeZone} onChange={(timeZone) => setForm({ ...form, timeZone })} />
          </Field>
          {form.action === 'ask' && (
          <Field label="Model">
            <Select value={form.model || DEFAULT} onValueChange={(v) => setForm({ ...form, model: v === DEFAULT ? '' : v })}>
              <SelectTrigger className="min-w-0 [&>span]:truncate">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={DEFAULT}>The model a new chat uses</SelectItem>
                {config.data?.models.map((m) => (
                  <SelectItem key={m.name} value={m.name}>
                    {m.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          )}
        </div>
        {form.action === 'ask' && tools.length > 0 && (
          <fieldset className="grid gap-2">
            <legend className="mb-1 text-sm font-medium">Tools</legend>
            <div className="flex flex-wrap gap-1.5">
              {tools.map((t) => (
                <label key={t.id} className="flex cursor-pointer items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary/10 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring">
                  <input
                    type="checkbox"
                    className="size-3.5 accent-primary outline-none"
                    checked={toolsOn.includes(t.id)}
                    onChange={(e) => setForm({ ...form, tools: e.target.checked ? [...toolsOn, t.id] : toolsOn.filter((x) => x !== t.id) })}
                  />
                  {t.title}
                </label>
              ))}
            </div>
          </fieldset>
        )}
        <div className="grid gap-3">
          <div className="flex items-start gap-2">
            <Switch id="task-same" checked={form.sameChat} disabled={form.action !== 'ask'} onCheckedChange={(sameChat) => setForm({ ...form, sameChat })} />
            <div className="grid gap-0.5">
              <Label htmlFor="task-same">One chat for every run</Label>
              <p className="text-xs text-muted-foreground">Each run reads the ones before (what changed since yesterday). Off: a new chat each time.</p>
            </div>
          </div>
          <div className="flex items-start gap-2">
            <Switch id="task-email" checked={form.email} disabled={!view.email && !form.email} onCheckedChange={(email) => setForm({ ...form, email })} />
            <div className="grid gap-0.5">
              <Label htmlFor="task-email">Email me the answer</Label>
              <p className="text-xs text-muted-foreground">{view.email ? `To ${view.email}.` : 'Email is not set up here (an admin sets it under Settings → Email).'}</p>
            </div>
          </div>
        </div>
        <Field
          label="Post to a channel (webhook)"
          hint={saved?.webhookSet ? 'Saved, never shown. A new URL replaces it; leave empty to keep it.' : `A Slack, Teams or Mattermost incoming webhook. Allowed hosts: ${view.webhookHosts || 'none'}.`}
        >
          <Input type="url" autoComplete="off" placeholder="https://hooks.slack.com/services/…" value={form.webhook} onChange={(e) => setForm({ ...form, webhook: e.target.value })} />
        </Field>
        {saved?.webhookSet && (
          <Button type="button" variant="link" className="h-auto justify-self-start p-0 text-xs" onClick={() => void api(`/api/tasks/${saved.id}`, { method: 'PATCH', body: { webhook: '' } }).then(() => onSaved()).then(() => toast.success('Webhook removed.'))}>
            Stop posting to the webhook
          </Button>
        )}
        {form.trigger === 'schedule' && <p className="text-xs text-muted-foreground">{describe(cronOf(schedule))}.</p>}
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            {saved ? 'Save' : 'Add task'}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
