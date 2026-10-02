import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CalendarClock, CircleCheck, CircleX, Loader2, Pencil, Play, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { TimeZonePicker } from '@/components/app/time-zone-picker'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { configQuery } from './chat/api'
import { blankSchedule, cronOf, describe, scheduleOf, weekdays, type Repeat, type Schedule } from './tasks-schedule'

interface Run {
  id: string
  startedAt: string
  finishedAt: string | null
  status: 'running' | 'done' | 'failed' | 'skipped'
  conversationId: string | null
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
}

interface TasksView {
  enabled: boolean
  perPerson: number
  minIntervalMinutes: number
  webhookHosts: string
  /** Where email goes, when the app can send it. */
  email: string | null
  tasks: Task[]
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
                    {describe(t.cron)} · {t.timeZone}
                    {t.model ? ` · ${t.model}` : ''}
                    {t.sameChat ? ' · one chat' : ''}
                    {t.email ? ' · email' : ''}
                    {t.webhookSet ? ' · webhook' : ''}
                  </CardDescription>
                </div>
                <Switch checked={t.enabled} disabled={toggle.isPending} onCheckedChange={() => toggle.mutate(t)} aria-label={`${t.name} on`} />
              </CardHeader>
              <CardContent className="grid gap-3 text-sm">
                <p dir="auto" className="line-clamp-3 whitespace-pre-wrap text-muted-foreground">
                  {t.prompt}
                </p>
                {t.enabled && t.nextRuns.length > 0 && (
                  <p className="text-xs text-muted-foreground">
                    Next: {t.nextRuns.map((r) => when(r, t.timeZone)).join(' · ')}
                  </p>
                )}
                {t.lastRun && <LastRun run={t.lastRun} />}
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" size="sm" onClick={() => run.mutate(t)} disabled={t.running || run.isPending}>
                    <Play /> Run now
                  </Button>
                  <Button variant="outline" size="sm" onClick={() => setEditing(t)}>
                    <Pencil /> Edit
                  </Button>
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
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {editing !== null && <TaskForm key={editing === 'new' ? 'new' : editing.id} saved={editing === 'new' ? null : editing} view={view} onClose={() => setEditing(null)} onSaved={changed} />}
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
      </p>
      {run.error && <p className="text-destructive-ink">{run.error}</p>}
      {run.delivery && <p className="text-muted-foreground">{run.delivery}</p>}
    </div>
  )
}

const DEFAULT = '(default)'

function TaskForm({ saved, view, onClose, onSaved }: { saved: Task | null; view: TasksView; onClose: () => void; onSaved: () => Promise<unknown> }) {
  const config = useQuery(configQuery)
  const [form, setForm] = useState({
    name: saved?.name ?? '',
    prompt: saved?.prompt ?? '',
    timeZone: saved?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone ?? 'UTC',
    model: saved?.model ?? '',
    tools: saved?.tools ?? null,
    sameChat: saved?.sameChat ?? false,
    email: saved?.email ?? false,
    webhook: '',
  })
  const [schedule, setSchedule] = useState<Schedule>(saved ? scheduleOf(saved.cron) : blankSchedule)
  const [error, setError] = useState<string | null>(null)
  const tools = config.data?.tools ?? []
  const toolsOn = form.tools ?? tools.filter((t) => t.onByDefault).map((t) => t.id)
  const save = useMutation({
    mutationFn: () => {
      const body = { ...form, cron: cronOf(schedule), webhook: form.webhook || (saved ? null : '') }
      return saved ? api(`/api/tasks/${saved.id}`, { method: 'PATCH', body }) : api('/api/tasks', { body })
    },
    onSuccess: async () => {
      await onSaved()
      toast.success(saved ? 'Task saved.' : 'Task added.', { description: 'Run it now to see what it brings.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const set = (patch: Partial<Schedule>) => setSchedule({ ...schedule, ...patch })
  return (
    <>
      <DialogHeader>
        <DialogTitle>{saved ? `Edit ${saved.name}` : 'New scheduled task'}</DialogTitle>
        <DialogDescription>Asked as you, with your model and tools, at the times you choose. Not more often than every {view.minIntervalMinutes} minutes.</DialogDescription>
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
        <Field label="What to ask" hint="As you would write it in the chat. With Argus on, it can read your repositories.">
          <Textarea dir="auto" required maxLength={20000} className="min-h-28" value={form.prompt} onChange={(e) => setForm({ ...form, prompt: e.target.value })} />
        </Field>
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
          <Field label="Time zone">
            <TimeZonePicker value={form.timeZone} onChange={(timeZone) => setForm({ ...form, timeZone })} />
          </Field>
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
        </div>
        {tools.length > 0 && (
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
            <Switch id="task-same" checked={form.sameChat} onCheckedChange={(sameChat) => setForm({ ...form, sameChat })} />
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
          <Button type="button" variant="link" className="h-auto justify-self-start p-0 text-xs" onClick={() => void api(`/api/tasks/${saved.id}`, { method: 'PATCH', body: { webhook: '' } }).then(onSaved).then(() => toast.success('Webhook removed.'))}>
            Stop posting to the webhook
          </Button>
        )}
        <p className="text-xs text-muted-foreground">{describe(cronOf(schedule))}.</p>
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
