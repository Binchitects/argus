import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AppWindow, Bot, Clock, Coins, ExternalLink, FolderGit2, GitMerge, Globe2, Hash, ListChecks, MessageSquare, Plus, SquareSlash, SquareTerminal, Target, Timer, Trash2, Users } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { ExportMenu, TimeWindow } from '@/components/app/time-window'
import { TimeChart } from '@/components/charts/time-chart'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, type ColumnDef } from '@/components/ui/data-table'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue, money } from '@/lib/format'
import { skillsQuery } from '@/lib/skills'
import { useTimeWindow, type TimeWindowState } from '@/lib/use-time-window'
import { assistantsQuery } from '../chat/api'
import { GroupChecklist, PeoplePicker, type Person } from '../chat/assistant-sharing'
import {
  goalLabels,
  hours,
  mayAdd,
  liveEvery,
  progressOf,
  projectQuery,
  roleLabels,
  type GoalStatus,
  type ItemKind,
  type Project,
  type ProjectPerson,
  type ProjectReport,
  type ProjectRole,
  type ProjectUse,
} from './api'

const right = { className: 'text-right tabular-nums' }

const peopleColumns: ColumnDef<ProjectPerson>[] = [
  { id: 'name', header: 'Person', accessorKey: 'name', cell: ({ row }) => <bdi className="font-medium">{row.original.name}</bdi> },
  { id: 'requests', header: 'Requests', accessorKey: 'requests', meta: right, cell: ({ row }) => formatValue(row.original.requests) },
  { id: 'tokens', header: 'Tokens', accessorKey: 'tokens', meta: right, cell: ({ row }) => formatValue(row.original.tokens, 'short') },
  { id: 'cost', header: 'Cost', accessorKey: 'cost', meta: right, cell: ({ row }) => money(row.original.cost) },
  { id: 'active', header: 'Active time', accessorKey: 'activeSeconds', meta: right, cell: ({ row }) => hours(row.original.activeSeconds) },
  { id: 'agent', header: 'Agent time', accessorKey: 'agentSeconds', meta: right, cell: ({ row }) => hours(row.original.agentSeconds) },
]

const usageColumns = (what: string): ColumnDef<ProjectUse>[] => [
  { id: 'name', header: what, accessorKey: 'name', cell: ({ row }) => <span className="font-medium">{row.original.name}</span> },
  { id: 'requests', header: 'Requests', accessorKey: 'requests', meta: right, cell: ({ row }) => formatValue(row.original.requests) },
  { id: 'tokens', header: 'Tokens', accessorKey: 'tokens', meta: right, cell: ({ row }) => formatValue(row.original.tokens, 'short') },
  { id: 'cost', header: 'Cost', accessorKey: 'cost', meta: right, cell: ({ row }) => money(row.original.cost) },
]
const modelColumns = usageColumns('Model')
const surfaceColumns = usageColumns('From')

/**
 * What differs where a project is shown: Arena's page (links to its chats, back to Projects when deleted) or Code Arena's
 * IDE (its chats and sessions opened in the IDE, the session open now put in or taken out).
 */
export interface ProjectHost {
  /** One of the person's chats, rooms or sessions in it, as a link or a button that opens it here. */
  chatLink: (c: Project['chats'][number], inner: ReactNode) => ReactNode
  /** The project was deleted: where to go. */
  onDeleted: () => void
  /** More for In it, first (Code Arena: the session open now). */
  inIt?: (p: Project) => ReactNode
  /** Where the assistants, skills and apps the person made are added, when not here (Arena's own page): its address. */
  itemsAt?: (p: Project) => string | null
}

/** A project in Arena's page: its address says which, and keeps the time window. */
export function ProjectPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const time = useTimeWindow()
  return (
    <ProjectView
      id={id}
      time={time}
      host={{
        chatLink: (c, inner) => (
          <Link to={`/chat/${c.id}`} className="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent">
            {inner}
          </Link>
        ),
        onDeleted: () => void navigate('/projects'),
      }}
    />
  )
}

/**
 * A project: what it used and did over the window, its goals and tasks, its people, what is in it, and (owners) its
 * settings. Arena's page and Code Arena's project tab both show it, so the two never differ.
 */
export function ProjectView({ id, time, host }: { id: string; time: TimeWindowState; host: ProjectHost }) {
  const project = useQuery(projectQuery(id))
  const [tab, setTab] = useState('overview')
  if (project.isPending) return <PageSkeleton />
  if (project.error) return <QueryError error={project.error} retry={() => project.refetch()} />
  const p = project.data
  const owner = p.role === 'owner'
  return (
    <>
      <PageHeader
        title={
          <span className="flex flex-wrap items-center gap-2">
            <bdi>{p.name}</bdi>
            {p.global && (
              <Badge variant="secondary" className="gap-1">
                <Globe2 className="size-3" aria-hidden="true" /> Global
              </Badge>
            )}
            {p.status === 'archived' && <Badge variant="outline">Archived</Badge>}
          </span>
        }
        description={
          <>
            {p.description && (
              <span dir="auto" className="block">
                {p.description}
              </span>
            )}
            <span className="text-xs">
              {roleLabels[p.role]} · made by {p.owner ?? 'a former person'}
              {p.dueOn ? ` · due ${new Date(`${p.dueOn}T00:00:00`).toLocaleDateString(undefined, { dateStyle: 'medium' })}` : ''}
            </span>
          </>
        }
      />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList className="max-w-full overflow-x-auto">
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="work">Goals and tasks</TabsTrigger>
          <TabsTrigger value="contents">In it</TabsTrigger>
          <TabsTrigger value="people">People</TabsTrigger>
          {owner && <TabsTrigger value="settings">Settings</TabsTrigger>}
        </TabsList>
        <TabsContent value="overview">
          <Overview project={p} time={time} />
        </TabsContent>
        <TabsContent value="work">
          <Work project={p} />
        </TabsContent>
        <TabsContent value="contents">
          <Contents project={p} host={host} />
        </TabsContent>
        <TabsContent value="people">
          <People project={p} />
        </TabsContent>
        {owner && (
          <TabsContent value="settings">
            <Settings project={p} onDeleted={host.onDeleted} />
          </TabsContent>
        )}
      </Tabs>
    </>
  )
}

/** The project's numbers over the window, as its report has them. */
function Overview({ project: p, time }: { project: Project; time: TimeWindowState }) {
  const report = useQuery({
    queryKey: ['projects', p.id, 'report', time.key],
    // What its chats and sessions use shows within seconds (the usage log takes it as each request ends).
    refetchInterval: liveEvery,
    queryFn: async ({ signal }) => {
      const r = time.resolved()
      return { ...(await api<ProjectReport>(`/api/projects/${p.id}/report?${time.query()}`, { signal })), fromMs: r.from.getTime(), toMs: r.to.getTime() }
    },
  })
  const r = report.data
  const t = r?.totals
  const progress = r?.progress
  const tasksAll = progress ? progress.tasks + progress.steps : 0
  const tasksDone = progress ? progress.tasksDone + progress.stepsDone : 0
  const done = progress ? progressOf({ goals: progress.goals, goalsDone: progress.goalsDone, tasks: tasksAll, tasksDone }) : null
  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-center gap-2">
        <TimeWindow value={time.range} onChange={time.set} />
        <ExportMenu path={`/api/projects/${p.id}/report/export`} query={time.query} />
      </div>
      {report.error && <QueryError error={report.error} retry={() => report.refetch()} />}
      {report.isPending && <Skeleton className="h-40" />}
      {r && t && progress && (
        <>
          {r.budget && <BudgetBar budget={r.budget} />}
          <StatGrid>
            <Stat icon={Coins} label="Cost" value={money(t.cost)} />
            <Stat icon={Hash} label="Tokens" value={formatValue(t.input + t.cached + t.output, 'short')} hint={`${formatValue(t.input + t.cached, 'short')} in (${formatValue(t.cached, 'short')} cached), ${formatValue(t.output, 'short')} out`} />
            <Stat icon={MessageSquare} label="Requests" value={formatValue(t.requests)} hint={`${r.models.length} model${r.models.length === 1 ? '' : 's'}`} />
            <Stat icon={Users} label="People" value={formatValue(t.people)} hint={`${formatValue(t.messages)} messages`} />
            <Stat icon={Clock} label="Active time" value={hours(t.activeSeconds)} hint="People's own time in its chats" />
            <Stat icon={Timer} label="Agent time" value={hours(t.agentSeconds)} hint="The models' time answering" />
            <Stat icon={Target} label="Goals done" value={`${progress.goalsDone} of ${progress.goals}`} hint={done !== null ? `${done}% done` : 'No goals yet'} />
            <Stat icon={ListChecks} label="Tasks done" value={`${tasksDone} of ${tasksAll}`} hint={`${progress.tasksDoneInRange} in this time; ${r.merged.length} merged`} />
          </StatGrid>
          {t.requests === 0 && t.messages === 0 ? (
            <EmptyState title="Nothing in this time">Its chats&apos; and sessions&apos; requests show up here within a minute.</EmptyState>
          ) : (
            <div className="stagger grid gap-4 xl:grid-cols-2">
              <Card>
                <CardHeader>
                  <CardTitle>Tokens</CardTitle>
                </CardHeader>
                <CardContent>
                  <TimeChart
                    series={[
                      { name: 'Input', points: r.series.map((s) => [Date.parse(s.at), s.input]) },
                      { name: 'Output', points: r.series.map((s) => [Date.parse(s.at), s.output]) },
                    ]}
                    stacked
                    bars
                    label={`${p.name}: tokens over time`}
                    from={r.fromMs}
                    to={r.toMs}
                  />
                </CardContent>
              </Card>
              <Card>
                <CardHeader>
                  <CardTitle>Cost</CardTitle>
                </CardHeader>
                <CardContent>
                  <TimeChart series={[{ name: 'Cost', points: r.series.map((s) => [Date.parse(s.at), s.cost]) }]} unit="currencyUSD" bars label={`${p.name}: cost over time`} from={r.fromMs} to={r.toMs} />
                </CardContent>
              </Card>
            </div>
          )}
          <Card>
            <CardHeader>
              <CardTitle>People</CardTitle>
              <CardDescription>{r.peopleHidden ? 'Your own share. Each person’s is for the project’s owners.' : 'Each person’s requests, what they cost, and their time in the project.'}</CardDescription>
            </CardHeader>
            <CardContent>
              <DataTable columns={peopleColumns} data={r.people} noun="people" label="People" compact getRowId={(x) => x.id ?? x.name} initialSorting={[{ id: 'cost', desc: true }]} />
            </CardContent>
          </Card>
          <div className="grid gap-4 xl:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>Models</CardTitle>
              </CardHeader>
              <CardContent>
                <DataTable columns={modelColumns} data={r.models} noun="models" label="Models" compact getRowId={(m) => m.name} />
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Where it came from</CardTitle>
              </CardHeader>
              <CardContent>
                <DataTable columns={surfaceColumns} data={r.surfaces} noun="sources" label="Where it came from" compact getRowId={(m) => m.name} />
              </CardContent>
            </Card>
          </div>
          <Merged report={r} hasRepositories={p.repositories.length > 0} />
        </>
      )}
    </div>
  )
}

function BudgetBar({ budget: b }: { budget: NonNullable<ProjectReport['budget']> }) {
  const over = b.spent >= b.monthly
  const near = !over && b.percent >= b.alertPercent
  const width = Math.min(100, b.percent)
  return (
    <div className="grid gap-1.5 rounded-lg border p-3">
      <div className="flex flex-wrap justify-between gap-2 text-sm">
        <span className="font-medium">
          Budget this month: {money(b.spent)} of {money(b.monthly)}
        </span>
        <span className={over ? 'font-medium text-destructive' : near ? 'font-medium text-warning-ink' : 'text-muted-foreground'}>
          {over ? (b.stopAtBudget ? 'Spent: its chats get no answers' : 'Spent') : `${b.percent}%`}
        </span>
      </div>
      {/* oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a styled bar; <meter> cannot be styled consistently */}
      <div className="h-2 overflow-hidden rounded-full bg-muted" role="meter" aria-label="Budget spent this month" aria-valuenow={Math.round(b.percent)} aria-valuemin={0} aria-valuemax={100}>
        <div className={over ? 'h-full bg-destructive' : near ? 'h-full bg-warning' : 'h-full bg-primary'} style={{ width: `${width}%` }} />
      </div>
    </div>
  )
}

function Merged({ report: r, hasRepositories }: { report: ProjectReport; hasRepositories: boolean }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <GitMerge className="size-4" aria-hidden="true" /> Merged changes
        </CardTitle>
        <CardDescription>{hasRepositories ? 'Merged in its repositories in this time, as you may read them.' : 'Add its repositories (In it) to see what was merged there.'}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-2">
        {r.mergedError && <Alert>{r.mergedError}</Alert>}
        {r.merged.length === 0 && hasRepositories && !r.mergedError && <p className="text-sm text-muted-foreground">Nothing merged in this time.</p>}
        {r.merged.length > 0 && (
          <ul className="grid divide-y" aria-label="Merged changes">
            {r.merged.map((m) => (
              <li key={`${m.repository}!${m.iid}`} className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 py-2 text-sm">
                <span className="text-xs text-muted-foreground tabular-nums">
                  {m.repository} !{m.iid}
                </span>
                {m.url ? (
                  <a href={m.url} target="_blank" rel="noreferrer" className="min-w-0 font-medium hover:underline">
                    <bdi>{m.title}</bdi> <ExternalLink className="inline size-3" aria-hidden="true" />
                  </a>
                ) : (
                  <bdi className="min-w-0 font-medium">{m.title}</bdi>
                )}
                <span className="ms-auto text-xs text-muted-foreground">
                  {m.author ?? ''}
                  {m.mergedAt ? ` · ${new Date(m.mergedAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}` : ''}
                </span>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  )
}

/** A change to the project and its page again; an error said as a toast. */
function useProjectChange(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ path, method = 'POST', body }: { path: string; method?: string; body?: unknown }) => api(`/api/projects/${id}${path}`, { method, body }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
}

/** Goals (milestones with a state and a due day) and tasks, for contributors to keep. */
function Work({ project: p }: { project: Project }) {
  const change = useProjectChange(p.id)
  const may = mayAdd(p.role) && p.status === 'active'
  const [goal, setGoal] = useState({ title: '', dueOn: '' })
  const [task, setTask] = useState({ title: '', goalId: '' })
  const people = p.members.filter((m) => m.userId)
  const addGoal = (e: FormEvent) => {
    e.preventDefault()
    if (!goal.title.trim()) return
    change.mutate({ path: '/goals', body: { title: goal.title, dueOn: goal.dueOn || undefined } }, { onSuccess: () => setGoal({ title: '', dueOn: '' }) })
  }
  const addTask = (e: FormEvent) => {
    e.preventDefault()
    if (!task.title.trim()) return
    change.mutate({ path: '/tasks', body: { title: task.title, goalId: task.goalId || undefined } }, { onSuccess: () => setTask({ title: '', goalId: task.goalId }) })
  }
  return (
    <div className="grid gap-6 xl:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Goals</CardTitle>
          <CardDescription>Milestones, each planned, doing, done or dropped. Done goals make the project&apos;s progress.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          {p.goals.length === 0 && <p className="text-sm text-muted-foreground">No goals yet.</p>}
          <ul className="grid gap-2" aria-label="Goals">
            {p.goals.map((g) => (
              <li key={g.id} className="flex flex-wrap items-center gap-2 rounded-lg border p-2.5">
                <div className="min-w-0 flex-1">
                  <bdi className={g.status === 'dropped' ? 'text-sm text-muted-foreground line-through' : 'text-sm font-medium'}>{g.title}</bdi>
                  <p className="text-xs text-muted-foreground">
                    {g.dueOn ? `Due ${new Date(`${g.dueOn}T00:00:00`).toLocaleDateString(undefined, { dateStyle: 'medium' })}` : 'No due day'}
                    {g.doneAt ? ` · done ${new Date(g.doneAt).toLocaleDateString(undefined, { dateStyle: 'medium' })}` : ''}
                  </p>
                </div>
                <Select value={g.status} disabled={!may} onValueChange={(status) => change.mutate({ path: `/goals/${g.id}`, method: 'PATCH', body: { status } })}>
                  <SelectTrigger className="h-8 w-32" aria-label={`State of ${g.title}`}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {(Object.keys(goalLabels) as GoalStatus[]).map((s) => (
                      <SelectItem key={s} value={s}>
                        {goalLabels[s]}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {may && (
                  <Button variant="ghost" size="icon-sm" aria-label={`Remove ${g.title}`} onClick={() => change.mutate({ path: `/goals/${g.id}`, method: 'DELETE' })}>
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
          {may && (
            <form className="flex flex-wrap items-end gap-2" onSubmit={addGoal} aria-label="New goal">
              <Input value={goal.title} maxLength={200} onChange={(e) => setGoal({ ...goal, title: e.target.value })} placeholder="A goal or milestone" aria-label="New goal" className="h-9 min-w-48 flex-1" />
              <Input type="date" value={goal.dueOn} onChange={(e) => setGoal({ ...goal, dueOn: e.target.value })} aria-label="Due" className="h-9 w-40" />
              <Button type="submit" size="sm" className="h-9" disabled={!goal.title.trim()}>
                <Plus /> Add
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Tasks</CardTitle>
          <CardDescription>What is to be done, of a goal or not, and who does it. The plans of its chats count as tasks too.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          {p.tasks.length === 0 && <p className="text-sm text-muted-foreground">No tasks yet.</p>}
          <ul className="grid gap-1" aria-label="Tasks">
            {p.tasks.map((t) => (
              <li key={t.id} className="flex flex-wrap items-center gap-2 rounded-md px-1 py-1.5 hover:bg-accent/50">
                <Checkbox
                  id={`task-${t.id}`}
                  checked={t.done}
                  disabled={!may}
                  onCheckedChange={(v) => change.mutate({ path: `/tasks/${t.id}`, method: 'PATCH', body: { done: v === true } })}
                  aria-label={`${t.title}: done`}
                />
                <label htmlFor={`task-${t.id}`} className={t.done ? 'min-w-0 flex-1 text-sm text-muted-foreground line-through' : 'min-w-0 flex-1 text-sm'}>
                  <bdi>{t.title}</bdi>
                  {t.goalId && <span className="ms-2 text-xs text-muted-foreground">{p.goals.find((g) => g.id === t.goalId)?.title}</span>}
                </label>
                <Select
                  value={t.assigneeId ?? 'none'}
                  disabled={!may}
                  onValueChange={(v) => change.mutate({ path: `/tasks/${t.id}`, method: 'PATCH', body: { assigneeId: v === 'none' ? '00000000-0000-0000-0000-000000000000' : v } })}
                >
                  <SelectTrigger className="h-8 w-36" aria-label={`Who does ${t.title}`}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="none">Nobody yet</SelectItem>
                    {[...people.map((m) => ({ id: m.userId!, name: m.name })), ...(t.assigneeId && !people.some((m) => m.userId === t.assigneeId) ? [{ id: t.assigneeId, name: t.assignee ?? '?' }] : [])].map((m) => (
                      <SelectItem key={m.id} value={m.id}>
                        {m.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {may && (
                  <Button variant="ghost" size="icon-sm" aria-label={`Remove ${t.title}`} onClick={() => change.mutate({ path: `/tasks/${t.id}`, method: 'DELETE' })}>
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
          {may && (
            <form className="flex flex-wrap items-end gap-2" onSubmit={addTask} aria-label="New task">
              <Input value={task.title} maxLength={300} onChange={(e) => setTask({ ...task, title: e.target.value })} placeholder="A task" aria-label="New task" className="h-9 min-w-48 flex-1" />
              {p.goals.length > 0 && (
                <Select value={task.goalId || 'none'} onValueChange={(v) => setTask({ ...task, goalId: v === 'none' ? '' : v })}>
                  <SelectTrigger className="h-9 w-40" aria-label="Its goal">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="none">No goal</SelectItem>
                    {p.goals.map((g) => (
                      <SelectItem key={g.id} value={g.id}>
                        {g.title}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
              <Button type="submit" size="sm" className="h-9" disabled={!task.title.trim()}>
                <Plus /> Add
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

const kindIcons = { chat: MessageSquare, room: Users, session: SquareTerminal } as const
const itemIcons: Record<ItemKind, typeof Bot> = { assistant: Bot, skill: SquareSlash, app: AppWindow }
const itemLabels: Record<ItemKind, string> = { assistant: 'Assistant', skill: 'Skill', app: 'App' }

/** Its repositories, its chats, rooms and sessions, and the assistants, skills and apps it uses. */
function Contents({ project: p, host }: { project: Project; host: ProjectHost }) {
  const change = useProjectChange(p.id)
  const may = mayAdd(p.role) && p.status === 'active'
  const owner = p.role === 'owner'
  const [repo, setRepo] = useState('')
  const [kind, setKind] = useState<ItemKind>('assistant')
  const [item, setItem] = useState('')
  // What the person made is listed by Arena's own page; elsewhere (Code Arena) they are added there.
  const itemsAt = host.itemsAt?.(p) ?? null
  const choosing = may && !itemsAt
  const assistants = useQuery({ ...assistantsQuery, enabled: choosing && kind === 'assistant' })
  const skills = useQuery({ ...skillsQuery, enabled: choosing && kind === 'skill' })
  const apps = useQuery({ queryKey: ['apps'], queryFn: ({ signal }) => api<{ id: string; name: string; mine: boolean }[]>('/api/apps', { signal }), enabled: choosing && kind === 'app' })
  const choices =
    kind === 'assistant'
      ? (assistants.data ?? []).filter((a) => a.canEdit).map((a) => ({ id: a.id, name: a.name }))
      : kind === 'skill'
        ? (skills.data?.skills ?? []).filter((s) => s.source === 'mine').map((s) => ({ id: s.id, name: `/${s.name} ${s.title}` }))
        : (apps.data ?? []).filter((a) => a.mine).map((a) => ({ id: a.id, name: a.name }))
  const open = choices.filter((c) => !p.items.some((i) => i.kind === kind && i.id === c.id))
  return (
    <div className="grid gap-6 xl:grid-cols-2">
      {host.inIt?.(p)}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <FolderGit2 className="size-4" aria-hidden="true" /> Repositories
          </CardTitle>
          <CardDescription>Code Arena&apos;s sessions in a clone of one go in this project by themselves; what is merged there counts to its progress.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          {p.repositories.length === 0 && <p className="text-sm text-muted-foreground">No repositories yet.</p>}
          <ul className="grid gap-1" aria-label="Repositories">
            {p.repositories.map((r) => (
              <li key={r.path} className="flex items-center gap-2 rounded-md px-1 py-1 text-sm">
                <span className="min-w-0 flex-1 truncate font-mono text-xs">
                  {r.host ? <span className="text-muted-foreground">{r.host}/</span> : null}
                  {r.path}
                </span>
                {may && (
                  <Button variant="ghost" size="icon-sm" aria-label={`Remove ${r.path}`} onClick={() => change.mutate({ path: `/repositories?path=${encodeURIComponent(r.path)}`, method: 'DELETE' })}>
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
          {may && (
            <form
              className="flex gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                if (repo.trim()) change.mutate({ path: '/repositories', body: { path: repo } }, { onSuccess: () => setRepo('') })
              }}
            >
              <Input value={repo} onChange={(e) => setRepo(e.target.value)} placeholder="group/name, or its address" aria-label="Repository" className="h-9" />
              <Button type="submit" size="sm" className="h-9" disabled={!repo.trim()}>
                <Plus /> Add
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Chats and sessions</CardTitle>
          <CardDescription>Put one of yours in from its menu (⋯ → Project), or with /project in Code Arena. A chat stays its owner&apos;s: others see only that it is here.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-2">
          {p.chats.length === 0 && p.otherChats.length === 0 && <p className="text-sm text-muted-foreground">No chats in it yet.</p>}
          <ul className="grid gap-0.5" aria-label="Chats in the project">
            {p.chats.map((c) => {
              const Icon = kindIcons[c.kind]
              return (
                <li key={c.id}>
                  {host.chatLink(
                    c,
                    <>
                      <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <bdi className="min-w-0 flex-1 truncate text-start">{c.title}</bdi>
                      {c.kind === 'session' && c.place && <span className="max-w-40 truncate text-xs text-muted-foreground">{c.place}</span>}
                    </>,
                  )}
                </li>
              )
            })}
          </ul>
          {p.otherChats.length > 0 && (
            <p className="text-xs text-muted-foreground">
              Also: {p.otherChats.map((o) => `${o.person} (${o.count})`).join(', ')}
            </p>
          )}
        </CardContent>
      </Card>
      <Card className="xl:col-span-2">
        <CardHeader>
          <CardTitle>Assistants, skills and apps</CardTitle>
          <CardDescription>What the project works with. Being here shares it with no one: who may not use one still may not.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          {p.items.length === 0 && <p className="text-sm text-muted-foreground">Nothing yet.</p>}
          <ul className="grid gap-1 sm:grid-cols-2" aria-label="Assistants, skills and apps">
            {p.items.map((i) => {
              const Icon = itemIcons[i.kind]
              return (
                <li key={`${i.kind}:${i.id}`} className="flex items-center gap-2 rounded-md border px-2 py-1.5 text-sm">
                  <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <bdi className="min-w-0 flex-1 truncate">{i.name}</bdi>
                  <Badge variant="outline">{itemLabels[i.kind]}</Badge>
                  {may && (i.mine || owner) && (
                    <Button variant="ghost" size="icon-sm" aria-label={`Remove ${i.name}`} onClick={() => change.mutate({ path: `/items/${i.kind}/${i.id}`, method: 'DELETE' })}>
                      <Trash2 />
                    </Button>
                  )}
                </li>
              )
            })}
          </ul>
          {may && itemsAt && (
            <p className="text-xs text-muted-foreground">
              Add one you made on{' '}
              <a href={itemsAt} target="_blank" rel="noreferrer" className="font-medium text-primary-ink underline underline-offset-2">
                the project&apos;s page in Arena
              </a>
              .
            </p>
          )}
          {choosing && (
            <form
              className="flex flex-wrap gap-2"
              onSubmit={(e) => {
                e.preventDefault()
                if (item) change.mutate({ path: '/items', body: { kind, itemId: item } }, { onSuccess: () => setItem('') })
              }}
            >
              <Select
                value={kind}
                onValueChange={(v) => {
                  setKind(v as ItemKind)
                  setItem('')
                }}
              >
                <SelectTrigger className="h-9 w-36" aria-label="Kind">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(Object.keys(itemLabels) as ItemKind[]).map((k) => (
                    <SelectItem key={k} value={k}>
                      {itemLabels[k]}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Select value={item} onValueChange={setItem}>
                <SelectTrigger className="h-9 min-w-48 flex-1" aria-label={`Your ${itemLabels[kind].toLowerCase()}`}>
                  <SelectValue placeholder={open.length ? `Choose one of yours` : `No ${itemLabels[kind].toLowerCase()} of yours to add`} />
                </SelectTrigger>
                <SelectContent>
                  {open.map((c) => (
                    <SelectItem key={c.id} value={c.id}>
                      {c.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Button type="submit" size="sm" className="h-9" disabled={!item}>
                <Plus /> Add
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

/** Its people and groups, each in a role; owners add and change them, anyone leaves. */
function People({ project: p }: { project: Project }) {
  const change = useProjectChange(p.id)
  const confirm = useConfirm()
  const owner = p.role === 'owner'
  const [people, setPeople] = useState<Person[]>([])
  const [groups, setGroups] = useState<string[]>([])
  const [role, setRole] = useState<ProjectRole>('contributor')
  return (
    <div className="grid gap-6 xl:grid-cols-[1fr_24rem]">
      <Card>
        <CardHeader>
          <CardTitle>People and groups</CardTitle>
          <CardDescription>
            {p.global ? 'A global project: everyone is a contributor. These are its owners and viewers by name, and who was added.' : 'Owners run it, contributors add to it, viewers see it.'} {p.owner} made it and is an owner always.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {p.members.length === 0 && <p className="text-sm text-muted-foreground">Nobody else is in it yet.</p>}
          <ul className="grid divide-y" aria-label="People and groups">
            {p.members.map((m) => (
              <li key={m.id} className="flex flex-wrap items-center gap-2 py-2">
                {m.groupId ? <Users className="size-4 text-muted-foreground" aria-label="A group" /> : null}
                <div className="min-w-0 flex-1">
                  <bdi className="text-sm font-medium">{m.name}</bdi>
                  {m.email && <p className="truncate text-xs text-muted-foreground">{m.email}</p>}
                </div>
                {owner ? (
                  <Select value={m.role} onValueChange={(v) => change.mutate({ path: `/members/${m.id}`, method: 'PATCH', body: { role: v } })}>
                    <SelectTrigger className="h-8 w-36" aria-label={`Role of ${m.name}`}>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {(Object.keys(roleLabels) as ProjectRole[]).map((r) => (
                        <SelectItem key={r} value={r}>
                          {roleLabels[r]}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                ) : (
                  <Badge variant="outline">{roleLabels[m.role]}</Badge>
                )}
                {owner && (
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label={`Take ${m.name} out`}
                    onClick={async () => {
                      if (await confirm({ title: `Take ${m.name} out of ${p.name}?`, description: 'Their chats stay in it, and stay theirs.', confirm: 'Take out', destructive: true }))
                        change.mutate({ path: `/members/${m.id}`, method: 'DELETE' })
                    }}
                  >
                    <Trash2 />
                  </Button>
                )}
              </li>
            ))}
          </ul>
        </CardContent>
      </Card>
      {owner && (
        <Card>
          <CardHeader>
            <CardTitle>Add people</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-4">
            <PeoplePicker chosen={people} onChange={setPeople} label="People to add" />
            <div className="grid gap-1.5">
              <span className="text-sm font-medium">Groups</span>
              <GroupChecklist chosen={groups} onChange={setGroups} label="Groups to add" />
            </div>
            <Field label="As">
              <Select value={role} onValueChange={(v) => setRole(v as ProjectRole)}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(Object.keys(roleLabels) as ProjectRole[]).map((r) => (
                    <SelectItem key={r} value={r}>
                      {roleLabels[r]}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Button
              disabled={people.length + groups.length === 0 || change.isPending}
              onClick={() =>
                change.mutate(
                  { path: '/members', body: { userIds: people.map((x) => x.id), groupIds: groups, role } },
                  {
                    onSuccess: () => {
                      setPeople([])
                      setGroups([])
                    },
                  },
                )
              }
            >
              <Plus /> Add {people.length + groups.length || ''}
            </Button>
          </CardContent>
        </Card>
      )}
    </div>
  )
}

/** Owners': its name and dates, whether it is global, its budget, archiving and deleting it. */
function Settings({ project: p, onDeleted }: { project: Project; onDeleted: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const start = {
    name: p.name,
    description: p.description,
    startsOn: p.startsOn ?? '',
    dueOn: p.dueOn ?? '',
    global: p.global,
    budget: p.monthlyBudget === null ? '' : String(p.monthlyBudget),
    alertPercent: String(p.alertPercent),
    stopAtBudget: p.stopAtBudget,
  }
  const [form, setForm] = useState(start)
  const dirty = JSON.stringify(form) !== JSON.stringify(start)
  const budget = form.budget.trim() === '' ? 0 : Number(form.budget)
  const alert = Number(form.alertPercent)
  const problem = !form.name.trim() ? 'Give it a name.' : Number.isNaN(budget) || budget < 0 ? 'A budget is a number of dollars.' : !(alert >= 1 && alert <= 100) ? 'The alert is at 1% to 100%.' : null
  const save = useMutation({
    mutationFn: (body: Record<string, unknown>) => api(`/api/projects/${p.id}`, { method: 'PATCH', body }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Saved')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/projects/${p.id}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      onDeleted()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <div className="grid max-w-2xl gap-6">
      <Card>
        <CardHeader>
          <CardTitle>The project</CardTitle>
        </CardHeader>
        <CardContent>
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault()
              if (problem) return
              save.mutate({
                name: form.name,
                description: form.description,
                startsOn: form.startsOn,
                dueOn: form.dueOn,
                global: form.global,
                monthlyBudget: budget,
                alertPercent: alert,
                stopAtBudget: form.stopAtBudget,
              })
            }}
          >
            <Field label="Name">
              <Input value={form.name} maxLength={100} onChange={(e) => setForm({ ...form, name: e.target.value })} />
            </Field>
            <Field label="What it is for">
              <Textarea value={form.description} maxLength={2000} rows={3} onChange={(e) => setForm({ ...form, description: e.target.value })} />
            </Field>
            <div className="flex flex-wrap gap-4">
              <Field label="Starts">
                <Input type="date" value={form.startsOn} onChange={(e) => setForm({ ...form, startsOn: e.target.value })} className="w-44" />
              </Field>
              <Field label="Due">
                <Input type="date" value={form.dueOn} onChange={(e) => setForm({ ...form, dueOn: e.target.value })} className="w-44" />
              </Field>
            </div>
            <div className="flex items-start justify-between gap-4 rounded-lg border p-3">
              <div className="grid gap-0.5">
                <label htmlFor="settings-global" className="text-sm font-medium">
                  Global project
                </label>
                <p className="text-xs text-muted-foreground">Everyone sees it and may add to it. Making one needs its permission.</p>
              </div>
              <Switch id="settings-global" checked={form.global} onCheckedChange={(v) => setForm({ ...form, global: v })} />
            </div>
            <fieldset className="grid gap-3 rounded-lg border p-3">
              <legend className="px-1 text-sm font-medium">Monthly budget</legend>
              <div className="flex flex-wrap gap-4">
                <Field label="Dollars a month" hint="Empty: no budget.">
                  <Input inputMode="decimal" value={form.budget} onChange={(e) => setForm({ ...form, budget: e.target.value })} className="w-36" />
                </Field>
                <Field label="Tell the owners at (%)">
                  <Input inputMode="numeric" value={form.alertPercent} onChange={(e) => setForm({ ...form, alertPercent: e.target.value })} className="w-28" />
                </Field>
              </div>
              <div className="flex items-start justify-between gap-4">
                <div className="grid gap-0.5">
                  <label htmlFor="settings-stop" className="text-sm font-medium">
                    Stop at the budget
                  </label>
                  <p className="text-xs text-muted-foreground">Once it is spent, its chats get no answers until the month ends or it is raised.</p>
                </div>
                <Switch id="settings-stop" checked={form.stopAtBudget} disabled={budget <= 0} onCheckedChange={(v) => setForm({ ...form, stopAtBudget: v })} />
              </div>
            </fieldset>
            {problem && dirty && (
              <p className="text-sm text-destructive" role="alert">
                {problem}
              </p>
            )}
            <div className="flex justify-end gap-2">
              <Button type="button" variant="ghost" disabled={!dirty} onClick={() => setForm(start)}>
                Undo
              </Button>
              <Button type="submit" disabled={!dirty || !!problem || save.isPending}>
                Save
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Archive or delete</CardTitle>
          <CardDescription>Archived, it is read only and its chats answer as before. Deleted, its chats stay their owners&apos; in no project, and what it used stays in the usage reports.</CardDescription>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => save.mutate({ status: p.status === 'archived' ? 'active' : 'archived' })}>
            {p.status === 'archived' ? 'Make it active' : 'Archive'}
          </Button>
          <Button
            variant="destructive"
            onClick={async () => {
              if (await confirm({ title: `Delete ${p.name}?`, description: 'Its goals, tasks and people go with it. This cannot be undone.', confirm: 'Delete', destructive: true })) remove.mutate()
            }}
          >
            <Trash2 /> Delete
          </Button>
        </CardContent>
      </Card>
    </div>
  )
}
