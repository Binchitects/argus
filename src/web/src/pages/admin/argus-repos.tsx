import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CalendarClock, CircleCheck, CircleX, Eraser, GitBranch, Loader2, MoreHorizontal, RefreshCw, RotateCw, ScrollText, Trash2 } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, selectColumn, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, agoSeconds } from '@/lib/format'
import { cn } from '@/lib/utils'
import { isWorking, progressPercent, progressWords, type RepoProgress } from './argus-progress'
import { RepoLogView } from './argus-repo-log'
import { ScheduleForm } from './argus-repo-schedule'

export interface IndexedBranch {
  branch: string
  default: boolean
  sha: string | null
  message: string | null
  committed_at: number | null
  indexed_at: number | null
  last_run_at: number | null
  stale: boolean
  timed_out: boolean
  symbols_failed: boolean
  error: string | null
  files: number
  symbols: number
}

/** A repository's state in one word, as Argus computes it. */
export type RepoState = 'indexing' | 'queued' | 'failed' | 'stale' | 'indexed' | 'never' | 'off'

export interface RepoRow {
  gitlab_id: number
  repo: string
  name: string
  group: string
  default_branch: string
  included: boolean
  /** Branches indexed besides the default one: names or globs. */
  branches: string[]
  seen_at: number | null
  /** Whether GitLab listed it the last time it was asked. */
  listed: boolean
  languages: { lang: string; name: string; files: number }[]
  /** Its main language, when known. */
  language: string | null
  /** Its own schedule; "" follows the schedule for all. */
  schedule: string
  /** The schedule it runs on (its own, or the one for all), in words. */
  schedule_words: string
  schedule_kind: 'pass' | 'off' | 'hours' | 'daily' | 'weekly'
  next_run_at: number | null
  last_run_at: number | null
  state: RepoState
  /** What went wrong on its last run, when something did. */
  problem: string | null
  /** Where the run going now is with it. */
  progress: RepoProgress | null
  indexed: IndexedBranch[]
}

interface ReposView {
  configured?: false
  new_repos: 'include' | 'exclude'
  global_branches: string[]
  schedule: { default: string; words: string; time_zone: string; zone_problem: string | null; pass_interval: number; next_pass_at: number | null }
  listed_at: number | null
  running: boolean
  pending: string[]
  repos: RepoRow[]
}

interface RemoteBranch {
  name: string
  sha: string
  message: string | null
  committed_at: string | null
  default: boolean
  protected: boolean
}

type BatchAction = 'include' | 'exclude' | 'reindex' | 'schedule' | 'add_branches' | 'remove'

interface BatchResult {
  action: BatchAction
  results: { gitlab_id: number; repo: string | null; ok: boolean; message: string }[]
}

const short = (sha: string | null) => (sha ? sha.slice(0, 8) : '')

/** Each state: its words, its badge, and where it sorts (what needs a look first). */
const states: Record<RepoState, { label: string; variant: 'success' | 'warning' | 'destructive' | 'secondary' | 'outline' | 'default'; order: number }> = {
  indexing: { label: 'Indexing', variant: 'default', order: 0 },
  queued: { label: 'Queued', variant: 'outline', order: 1 },
  failed: { label: 'Failed', variant: 'destructive', order: 2 },
  stale: { label: 'Out of date', variant: 'warning', order: 3 },
  never: { label: 'Not indexed yet', variant: 'secondary', order: 4 },
  indexed: { label: 'Indexed', variant: 'success', order: 5 },
  off: { label: 'Left out', variant: 'outline', order: 6 },
}

type StatusFilter = 'all' | RepoState | 'unlisted'

/** "a, b, c and 4 more". */
function names(rows: RepoRow[], max = 6): string {
  const shown = rows.slice(0, max).map((r) => r.repo)
  return rows.length > max ? `${shown.join(', ')} and ${rows.length - max} more` : shown.join(', ')
}

const noun = (n: number) => (n === 1 ? '1 repository' : `${n.toLocaleString()} repositories`)

/**
 * Admin → Indexing → Repositories: what GitLab lists, found by name, path or group and narrowed
 * by state, group, language and whether it is indexed; each one's state live while it is
 * indexed, its schedule, branches and log; and changes to many at once, with an outcome for each.
 */
export function RepositoriesCard({ gitlabUrl }: { gitlabUrl: string | null }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const repos = useQuery({
    queryKey: ['admin', 'argus', 'repos'],
    queryFn: ({ signal }) => api<ReposView>('/api/admin/argus/repos', { signal }),
    refetchInterval: (q) => (q.state.data?.running || q.state.data?.pending.length ? 3000 : 60_000),
  })
  // When the app's scheduled pass comes next (in this platform the app starts the passes).
  const pass = useQuery({ queryKey: ['admin', 'argus', 'schedule'], queryFn: ({ signal }) => api<{ nextRuns: string[] }>('/api/admin/argus/schedule', { signal }) })
  const [branchesOf, setBranchesOf] = useState<RepoRow | null>(null)
  const [logOf, setLogOf] = useState<RepoRow | null>(null)
  const [scheduleOf, setScheduleOf] = useState<RepoRow[] | 'all' | null>(null)
  const [addBranchesTo, setAddBranchesTo] = useState<RepoRow[] | null>(null)
  const [outcome, setOutcome] = useState<{ title: string; result: BatchResult } | null>(null)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const [status, setStatus] = useState<StatusFilter>('all')
  const [group, setGroup] = useState('all')
  const [language, setLanguage] = useState('all')
  const [enabled, setEnabled] = useState<'all' | 'on' | 'off'>('all')

  const changed = () =>
    Promise.all([queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'repos'] }), queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'status'] })])
  const choose = useMutation({
    mutationFn: ({ rows, included }: { rows: RepoRow[]; included: boolean }) =>
      Promise.all(rows.map((r) => api<{ deferred: boolean }>(`/api/admin/argus/repos/${r.gitlab_id}`, { method: 'PATCH', body: { included } }))),
    onSuccess: async (res, { rows, included }) => {
      await changed()
      const what = rows.length === 1 ? rows[0]!.repo : noun(rows.length)
      if (included) toast.success(`${what} will be indexed`, { description: 'At the next run, or press Update.' })
      else toast.success(`${what} left out of the index`, { description: res.some((r) => r.deferred) ? 'Removed once the run going now ends.' : 'Its files and symbols are removed.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const update = useMutation({
    mutationFn: (r: RepoRow) => api<{ status: string; queued: number }>('/api/admin/argus/index', { body: { repo: r.repo } }),
    onSuccess: async (res, r) => {
      await changed()
      if (res.status === 'started') toast.success(`Updating ${r.repo}`, { description: 'From its latest commits: only what changed is read again.' })
      else if (res.status === 'already_queued') toast(`${r.repo} is already waiting its turn`)
      else toast.success(`${r.repo} is queued`, { description: 'It is updated when the run going now ends.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const discover = useMutation({
    mutationFn: () => api<ReposView & { found?: { new: number; moved: number; set_aside: number } }>('/api/admin/argus/repos/discover', { method: 'POST' }),
    onSuccess: async (v) => {
      await changed()
      const moved = v.found?.moved ? ` ${noun(v.found.moved)} found again under a new id, with their index.` : ''
      toast.success(`GitLab lists ${v.repos.filter((r) => r.listed).length} repositories`, { description: `${v.found?.new ?? 0} new.${moved}` })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const policy = useMutation({
    mutationFn: (body: { newRepos?: string; schedule?: string; timeZone?: string }) => api('/api/admin/argus/repos/settings', { method: 'PUT', body }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const scheduleOne = useMutation({
    mutationFn: ({ r, schedule }: { r: RepoRow; schedule: string }) => api(`/api/admin/argus/repos/${r.gitlab_id}`, { method: 'PATCH', body: { schedule } }),
    onSuccess: async (_, { r }) => {
      await changed()
      setScheduleOf(null)
      toast.success(`Schedule of ${r.repo} saved`)
    },
    onError: (e) => setDialogError(errorMessage(e)),
  })
  const batch = useMutation({
    mutationFn: ({ rows, action, schedule, branches }: { rows: RepoRow[]; action: BatchAction; schedule?: string; branches?: string[]; title: string }) =>
      api<BatchResult>('/api/admin/argus/repos/batch', { body: { ids: rows.map((r) => r.gitlab_id), action, schedule, branches } }),
    onSuccess: async (result, { title }) => {
      await changed()
      setScheduleOf(null)
      setAddBranchesTo(null)
      setOutcome({ title, result })
    },
    onError: (e) => {
      if (scheduleOf || addBranchesTo) setDialogError(errorMessage(e))
      else toast.error(errorMessage(e))
    },
  })
  // Its index removed now: kept in (built anew, at once) or left out.
  const removeIndex = useMutation({
    mutationFn: async ({ r, leaveOut }: { r: RepoRow; leaveOut: boolean }) => {
      const res = await api<{ removed: number }>(`/api/admin/argus/repos/${r.gitlab_id}/index/remove`, { body: { leaveOut } })
      if (!leaveOut) await api('/api/admin/argus/index', { body: { repo: r.repo } })
      return res
    },
    onSuccess: async (_, { r, leaveOut }) => {
      await changed()
      toast.success(leaveOut ? `${r.repo}: index removed` : `${r.repo}: rebuilding its index`, {
        description: leaveOut ? 'Left out of the index; answers no longer cover it. Turn it on again to index it anew.' : 'Its old index is gone; it is read again from its latest commits.',
      })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const askRemoveIndex = async (r: RepoRow, leaveOut: boolean) => {
    if (
      await confirm(
        leaveOut
          ? { title: `Remove the index of ${r.repo}?`, description: "Every branch's files and symbols are removed, and it is left out of the index. Turn it on again to index it anew.", confirm: 'Remove index', destructive: true }
          : { title: `Rebuild the index of ${r.repo}?`, description: 'Its index is removed and read again from its latest commits; until then, answers do not cover it.', confirm: 'Rebuild' },
      )
    )
      removeIndex.mutate({ r, leaveOut })
  }
  const leaveOut = async (rows: RepoRow[]) => {
    const what = rows.length === 1 ? rows[0]!.repo : noun(rows.length)
    if (await confirm({ title: `Leave ${what} out of the index?`, description: 'Their files and symbols are removed, and answers no longer cover them. Choose them again to index them anew.', confirm: 'Leave out', destructive: true }))
      choose.mutate({ rows, included: false })
  }
  /** A change to many: asked first, then each one's outcome shown. */
  const askBatch = async (rows: RepoRow[], action: 'include' | 'exclude' | 'reindex' | 'remove') => {
    const ask = {
      include: { title: `Index ${noun(rows.length)}?`, description: `${names(rows)}. The next run indexes them, or Update them now.`, confirm: 'Index them', destructive: false, done: 'Chosen for the index' },
      exclude: { title: `Leave ${noun(rows.length)} out of the index?`, description: `${names(rows)}. Their files and symbols are removed, and answers no longer cover them.`, confirm: 'Leave them out', destructive: true, done: 'Left out of the index' },
      reindex: { title: `Update ${noun(rows.length)} now?`, description: `${names(rows)}. They are read from their latest commits together, now or when the run going ends; only what changed is read again.`, confirm: 'Update now', destructive: false, done: 'Updated now' },
      remove: { title: `Remove ${noun(rows.length)}?`, description: `${names(rows)}. Their index is removed and they are left out; one GitLab no longer lists is forgotten.`, confirm: 'Remove', destructive: true, done: 'Removed' },
    }[action]
    if (await confirm(ask)) batch.mutate({ rows, action, title: ask.done })
  }

  const view = repos.data
  const passNext = pass.data?.nextRuns?.[0] ? new Date(pass.data.nextRuns[0]).getTime() / 1000 : (view?.schedule.next_pass_at ?? null)
  const groups = useMemo(() => [...new Set((view?.repos ?? []).map((r) => r.group).filter(Boolean))].sort(), [view])
  const languages = useMemo(() => [...new Set((view?.repos ?? []).map((r) => r.language).filter((l): l is string => !!l))].sort(), [view])
  const counts = useMemo(() => {
    const c: Partial<Record<StatusFilter, number>> = {}
    for (const r of view?.repos ?? []) {
      c[r.state] = (c[r.state] ?? 0) + 1
      if (!r.listed) c.unlisted = (c.unlisted ?? 0) + 1
    }
    return c
  }, [view])
  const shown = useMemo(
    () =>
      (view?.repos ?? []).filter(
        (r) =>
          (status === 'all' || (status === 'unlisted' ? !r.listed : r.state === status)) &&
          (group === 'all' || r.group === group || r.group.startsWith(`${group}/`)) &&
          (language === 'all' || r.language === language) &&
          (enabled === 'all' || r.included === (enabled === 'on')),
      ),
    [view, status, group, language, enabled],
  )
  const filtered = status !== 'all' || group !== 'all' || language !== 'all' || enabled !== 'all'

  const columns: ColumnDef<RepoRow>[] = [
    selectColumn<RepoRow>(),
    {
      id: 'included',
      accessorFn: (r) => (r.included ? 1 : 0),
      header: ({ column }) => <SortHeader column={column} title="Indexed" />,
      cell: ({ row: { original: r } }) => (
        <Switch checked={r.included} disabled={choose.isPending} onCheckedChange={(on) => (on ? choose.mutate({ rows: [r], included: true }) : void leaveOut([r]))} aria-label={`Index ${r.repo}`} />
      ),
    },
    {
      id: 'repo',
      // Found by its name, its path and its group.
      accessorFn: (r) => `${r.repo} ${r.name} ${r.group}`,
      sortingFn: (a, b) => a.original.repo.localeCompare(b.original.repo),
      header: ({ column }) => <SortHeader column={column} title="Repository" />,
      cell: ({ row: { original: r } }) => (
        <div className="grid min-w-0 gap-1">
          <span className="min-w-0">
            <span className="font-medium [overflow-wrap:anywhere]">{r.name}</span>
            <span className="block text-xs text-muted-foreground [overflow-wrap:anywhere]">
              {r.group}
              {r.group && r.language && ' · '}
              {r.language && <span title={r.languages.map((l) => `${l.name}: ${l.files.toLocaleString()} files`).join('\n')}>{r.language}</span>}
            </span>
          </span>
          <span className="flex flex-wrap items-center gap-1">
            <Badge variant="secondary" className="font-mono">
              {r.default_branch}
            </Badge>
            {r.branches.map((b) => (
              <Badge key={b} variant="outline" className="font-mono">
                {b}
              </Badge>
            ))}
            <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-xs" onClick={() => setBranchesOf(r)} aria-label={`Branches of ${r.repo}`}>
              <GitBranch className="size-3.5" /> Branches
            </Button>
          </span>
        </div>
      ),
    },
    {
      id: 'state',
      accessorFn: (r) => states[r.state].order,
      header: ({ column }) => <SortHeader column={column} title="Status" />,
      cell: ({ row: { original: r } }) => <StatusCell r={r} />,
    },
    {
      id: 'schedule',
      accessorFn: (r) => r.next_run_at ?? (r.schedule_kind === 'pass' && r.included ? (passNext ?? Number.MAX_SAFE_INTEGER - 1) : Number.MAX_SAFE_INTEGER),
      header: ({ column }) => <SortHeader column={column} title="Schedule" />,
      cell: ({ row: { original: r } }) => <ScheduleCell r={r} passNext={passNext} />,
    },
    {
      id: 'index',
      header: 'Index',
      accessorFn: (r) => r.indexed.map((b) => `${b.branch} ${b.sha ?? ''} ${b.message ?? ''}`).join(' '),
      cell: ({ row: { original: r } }) => <IndexState repo={r} gitlabUrl={gitlabUrl} />,
    },
    {
      id: 'actions',
      enableSorting: false,
      header: () => <span className="sr-only">Actions</span>,
      cell: ({ row: { original: r } }) => (
        <span className="flex items-center justify-end gap-1">
          <Button variant="outline" size="sm" disabled={!r.included || !r.listed || update.isPending} onClick={() => update.mutate(r)} aria-label={`Update the index of ${r.repo}`}>
            <RotateCw /> Update
          </Button>
          <Button variant="ghost" size="icon-sm" onClick={() => setLogOf(r)} aria-label={`Log of ${r.repo}`}>
            <ScrollText />
          </Button>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon-sm" aria-label={`More for ${r.repo}`}>
                <MoreHorizontal />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={() => setScheduleOf([r])}>
                <CalendarClock /> Schedule…
              </DropdownMenuItem>
              <DropdownMenuItem onSelect={() => setBranchesOf(r)}>
                <GitBranch /> Branches…
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem disabled={view?.running || !r.included || r.indexed.length === 0} onSelect={() => void askRemoveIndex(r, false)}>
                <Eraser /> Rebuild index
              </DropdownMenuItem>
              <DropdownMenuItem variant="destructive" disabled={view?.running || (r.indexed.length === 0 && !r.included)} onSelect={() => void askRemoveIndex(r, true)}>
                <Trash2 /> Remove index
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </span>
      ),
    },
  ]

  const scheduleRows = scheduleOf === 'all' ? null : scheduleOf
  return (
    <Card aria-label="Repositories">
      <CardHeader className="flex flex-row flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <CardTitle>Repositories</CardTitle>
          <CardDescription>
            What GitLab lists, and which are indexed: each at its default branch and the branches you add, on the schedule for all or its own. Find them by name, path or group,
            narrow them down, and change several at once.
          </CardDescription>
        </div>
        <Button variant="outline" size="sm" onClick={() => discover.mutate()} loading={discover.isPending}>
          <RefreshCw /> Refresh from GitLab
        </Button>
      </CardHeader>
      {/* One column as wide as the card: the table scrolls inside it rather than widening the page. */}
      <CardContent className="grid grid-cols-[minmax(0,1fr)] gap-4">
        {repos.error && <Alert variant="destructive">{errorMessage(repos.error)}</Alert>}
        {view && (
          <div className="flex flex-wrap items-center gap-x-6 gap-y-2 text-sm">
            <span className="flex flex-wrap items-center gap-2">
              <span className="text-muted-foreground">Schedule for all</span>
              <span className="font-medium">{view.schedule.words}</span>
              <Button size="sm" variant="outline" className="h-7" onClick={() => setScheduleOf('all')}>
                Change
              </Button>
            </span>
            <span className="flex items-center gap-2">
              <span className="text-muted-foreground">New repositories</span>
              <Select value={view.new_repos} onValueChange={(v) => policy.mutate({ newRepos: v })}>
                <SelectTrigger size="sm" className="w-44" aria-label="New repositories">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="include">Index them</SelectItem>
                  <SelectItem value="exclude">Leave them out</SelectItem>
                </SelectContent>
              </Select>
            </span>
          </div>
        )}
        {view?.schedule.zone_problem && <Alert variant="warning">{view.schedule.zone_problem}</Alert>}
        {view && view.global_branches.length > 0 && (
          <p className="text-xs text-muted-foreground">
            Every repository also has these branches indexed (ARGUS_INDEX_BRANCHES): <span className="font-mono">{view.global_branches.join(' ')}</span>
          </p>
        )}
        {view && view.repos.length > 0 && (
          <fieldset className="flex flex-wrap gap-1.5">
            <legend className="sr-only">Repositories by state</legend>
            {(Object.keys(states) as RepoState[])
              .filter((s) => counts[s])
              .map((s) => (
                <Button key={s} size="sm" variant={status === s ? 'secondary' : 'ghost'} className="h-7 gap-1.5 px-2 text-xs" aria-pressed={status === s} onClick={() => setStatus(status === s ? 'all' : s)}>
                  {states[s].label} <span className="tabular-nums text-muted-foreground">{counts[s]}</span>
                </Button>
              ))}
            {counts.unlisted ? (
              <Button size="sm" variant={status === 'unlisted' ? 'secondary' : 'ghost'} className="h-7 gap-1.5 px-2 text-xs" aria-pressed={status === 'unlisted'} onClick={() => setStatus(status === 'unlisted' ? 'all' : 'unlisted')}>
                Not in GitLab <span className="tabular-nums text-muted-foreground">{counts.unlisted}</span>
              </Button>
            ) : null}
          </fieldset>
        )}
        <DataTable
          columns={columns}
          data={view ? shown : undefined}
          loading={repos.isPending}
          noun="repositories"
          searchPlaceholder="Find by name, path, group or commit…"
          getRowId={(r) => String(r.gitlab_id)}
          initialSorting={[{ id: 'repo', desc: false }]}
          empty={
            filtered ? (
              <span className="grid justify-items-center gap-2">
                No repositories match these filters.
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    setStatus('all')
                    setGroup('all')
                    setLanguage('all')
                    setEnabled('all')
                  }}
                >
                  Clear the filters
                </Button>
              </span>
            ) : (
              <p className="text-sm text-muted-foreground">No repositories yet: Refresh from GitLab lists them, and so does every index pass.</p>
            )
          }
          toolbar={
            view && (
              <>
                <Select value={status} onValueChange={(v) => setStatus(v as StatusFilter)}>
                  <SelectTrigger className="w-full sm:w-40" aria-label="Status">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">Every state</SelectItem>
                    {(Object.keys(states) as RepoState[]).map((s) => (
                      <SelectItem key={s} value={s}>
                        {states[s].label}
                      </SelectItem>
                    ))}
                    <SelectItem value="unlisted">Not in GitLab</SelectItem>
                  </SelectContent>
                </Select>
                {groups.length > 1 && (
                  <Select value={group} onValueChange={setGroup}>
                    <SelectTrigger className="w-full sm:w-44" aria-label="Group">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="all">Every group</SelectItem>
                      {groups.map((g) => (
                        <SelectItem key={g} value={g}>
                          {g}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
                {languages.length > 1 && (
                  <Select value={language} onValueChange={setLanguage}>
                    <SelectTrigger className="w-full sm:w-40" aria-label="Language">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="all">Every language</SelectItem>
                      {languages.map((l) => (
                        <SelectItem key={l} value={l}>
                          {l}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
                <Select value={enabled} onValueChange={(v) => setEnabled(v as 'all' | 'on' | 'off')}>
                  <SelectTrigger className="w-full sm:w-40" aria-label="Indexed or not">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="all">Indexed or not</SelectItem>
                    <SelectItem value="on">Indexed</SelectItem>
                    <SelectItem value="off">Left out</SelectItem>
                  </SelectContent>
                </Select>
              </>
            )
          }
          bulk={(selected, table) => {
            const matching = table.getFilteredRowModel().rows
            return (
              <>
                {selected.length < matching.length && (
                  <Button size="sm" variant="ghost" onClick={() => table.setRowSelection(Object.fromEntries(matching.map((r) => [r.id, true])))}>
                    Select all {matching.length.toLocaleString()}
                  </Button>
                )}
                <Button size="sm" variant="outline" onClick={() => void askBatch(selected, 'reindex')}>
                  <RotateCw /> Update now
                </Button>
                <Button size="sm" variant="outline" onClick={() => void askBatch(selected, 'include')}>
                  Index them
                </Button>
                <Button size="sm" variant="outline" onClick={() => void askBatch(selected, 'exclude')}>
                  Leave them out
                </Button>
                <Button size="sm" variant="outline" onClick={() => setScheduleOf(selected)}>
                  <CalendarClock /> Schedule…
                </Button>
                <Button size="sm" variant="outline" onClick={() => setAddBranchesTo(selected)}>
                  <GitBranch /> Add branches…
                </Button>
                <Button size="sm" variant="outline" className="text-destructive-ink" onClick={() => void askBatch(selected, 'remove')}>
                  <Trash2 /> Remove…
                </Button>
              </>
            )
          }}
        />
      </CardContent>
      <Dialog open={branchesOf !== null} onOpenChange={(o) => !o && setBranchesOf(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {branchesOf && <BranchesForm key={branchesOf.gitlab_id} repo={branchesOf} onClose={() => setBranchesOf(null)} onSaved={changed} />}
        </DialogContent>
      </Dialog>
      <Dialog open={logOf !== null} onOpenChange={(o) => !o && setLogOf(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-3xl">{logOf && <RepoLogView gitlabId={logOf.gitlab_id} repo={logOf.repo} />}</DialogContent>
      </Dialog>
      <Dialog
        open={scheduleOf !== null}
        onOpenChange={(o) => {
          if (!o) setScheduleOf(null)
          setDialogError(null)
        }}
      >
        <DialogContent className="sm:max-w-lg">
          {scheduleOf === 'all' && view && (
            <ScheduleForm
              title="Schedule for all"
              description="Every repository without a schedule of its own is reindexed on this one. With each scheduled pass, it is brought up to date when the Schedule above starts a pass."
              initial={view.schedule.default}
              timeZone={view.schedule.time_zone}
              pending={policy.isPending}
              error={dialogError}
              submit="Save"
              onCancel={() => setScheduleOf(null)}
              onSubmit={(schedule, timeZone) =>
                policy.mutate(
                  { schedule, timeZone },
                  {
                    onSuccess: () => {
                      setScheduleOf(null)
                      toast.success('Schedule for all saved')
                    },
                    onError: (e) => setDialogError(errorMessage(e)),
                  },
                )
              }
            />
          )}
          {scheduleRows && view && (
            <ScheduleForm
              key={scheduleRows.map((r) => r.gitlab_id).join()}
              title={scheduleRows.length === 1 ? `Schedule of ${scheduleRows[0]!.repo}` : `Schedule of ${noun(scheduleRows.length)}`}
              description={
                scheduleRows.length === 1
                  ? `When it is brought up to date by itself. Times of day are in ${view.schedule.time_zone}; pushes and Update still update it at once.`
                  : `${names(scheduleRows)}. Times of day are in ${view.schedule.time_zone}.`
              }
              initial={scheduleRows.length === 1 ? scheduleRows[0]!.schedule : ''}
              defaultWords={view.schedule.words}
              pending={scheduleOne.isPending || batch.isPending}
              error={dialogError}
              submit={scheduleRows.length === 1 ? 'Save' : `Apply to ${noun(scheduleRows.length)}`}
              onCancel={() => setScheduleOf(null)}
              onSubmit={(schedule) =>
                scheduleRows.length === 1
                  ? scheduleOne.mutate({ r: scheduleRows[0]!, schedule })
                  : batch.mutate({ rows: scheduleRows, action: 'schedule', schedule, title: 'Schedule changed' })
              }
            />
          )}
        </DialogContent>
      </Dialog>
      <Dialog
        open={addBranchesTo !== null}
        onOpenChange={(o) => {
          if (!o) setAddBranchesTo(null)
          setDialogError(null)
        }}
      >
        <DialogContent className="sm:max-w-lg">
          {addBranchesTo && (
            <AddBranchesForm
              rows={addBranchesTo}
              pending={batch.isPending}
              error={dialogError}
              onCancel={() => setAddBranchesTo(null)}
              onSubmit={(branches) => batch.mutate({ rows: addBranchesTo, action: 'add_branches', branches, title: 'Branches added' })}
            />
          )}
        </DialogContent>
      </Dialog>
      <Dialog open={outcome !== null} onOpenChange={(o) => !o && setOutcome(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">{outcome && <Outcome title={outcome.title} result={outcome.result} onClose={() => setOutcome(null)} />}</DialogContent>
      </Dialog>
    </Card>
  )
}

/** A repository's state, and while a run is on it, where it is; what went wrong, when something did. */
function StatusCell({ r }: { r: RepoRow }) {
  const p = r.progress
  const working = isWorking(p)
  const percent = progressPercent(p)
  const state = states[r.state]
  return (
    <div className="grid min-w-44 gap-1 text-xs">
      <span className="flex flex-wrap items-center gap-1">
        <Badge variant={state.variant}>
          {working && <Loader2 className="animate-spin" aria-hidden="true" />}
          {state.label}
        </Badge>
        {!r.listed && (
          <Badge variant="warning" title="GitLab did not list it the last time it was asked: a token that cannot see it, or a repository moved or deleted.">
            Not in GitLab
          </Badge>
        )}
      </span>
      {working && p && (
        <>
          <span className="text-foreground">{progressWords(p)}</span>
          {percent !== null && (
            <progress value={percent} max={100} aria-label={`${r.repo}: how far`} className="h-1.5 w-full max-w-56 appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary" />
          )}
        </>
      )}
      {p?.state === 'done' && p.message && <span className={cn('text-muted-foreground', p.outcome === 'warning' && 'text-warning-ink')}>{p.message}</span>}
      {p?.state === 'failed' && p.message && <span className="text-destructive-ink [overflow-wrap:anywhere]">{p.message}</span>}
      {!p && r.state === 'failed' && r.problem && <span className="line-clamp-2 text-destructive-ink [overflow-wrap:anywhere]" title={r.problem}>{r.problem}</span>}
    </div>
  )
}

/** The schedule it runs on, and when it next runs and last ran. */
function ScheduleCell({ r, passNext }: { r: RepoRow; passNext: number | null }) {
  const next = !r.included
    ? null
    : r.next_run_at
      ? `next ${ago(r.next_run_at)}`
      : r.schedule_kind === 'pass'
        ? passNext
          ? `next pass ${ago(passNext)}`
          : 'no passes are scheduled'
        : null
  return (
    <div className="grid min-w-36 gap-0.5 text-xs">
      <span className="text-foreground">
        {r.schedule_words}
        {!r.schedule && <span className="text-muted-foreground"> (for all)</span>}
      </span>
      <span className="text-muted-foreground">
        {next ? `${next} · ` : ''}last {agoSeconds(r.last_run_at)}
      </span>
    </div>
  )
}

/** Each indexed branch of a repository: its commit (hash and message), when, how its last run went. */
function IndexState({ repo, gitlabUrl }: { repo: RepoRow; gitlabUrl: string | null }) {
  return (
    <div className="grid min-w-56 gap-1.5 text-xs">
      {repo.indexed.length === 0 && <span className="text-muted-foreground">{repo.included ? 'Not indexed yet: the next run, or Update.' : 'Not indexed.'}</span>}
      {repo.indexed.map((b) => (
        <div key={b.branch} className="grid min-w-0 gap-0.5">
          <span className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
            <span className="font-mono text-foreground">{b.branch}</span>
            {b.sha &&
              (gitlabUrl ? (
                <a href={`${gitlabUrl.replace(/\/$/, '')}/${repo.repo}/-/commit/${b.sha}`} target="_blank" rel="noreferrer" className="font-mono text-primary-ink underline underline-offset-2">
                  {short(b.sha)}
                </a>
              ) : (
                <span className="font-mono">{short(b.sha)}</span>
              ))}
            <BranchState b={b} />
          </span>
          {b.message && (
            <span dir="auto" className="truncate text-muted-foreground" title={b.message}>
              {b.message}
            </span>
          )}
          <span className="text-muted-foreground">
            {b.committed_at ? `committed ${agoSeconds(b.committed_at)} · ` : ''}checked {agoSeconds(b.last_run_at)} · {b.files.toLocaleString()} files, {b.symbols.toLocaleString()} symbols
          </span>
          {/* One branch: its error is the repository's, said under Status. */}
          {b.error && repo.indexed.length > 1 && <span className="text-destructive-ink [overflow-wrap:anywhere]">{b.error}</span>}
        </div>
      ))}
    </div>
  )
}

function BranchState({ b }: { b: IndexedBranch }) {
  if (b.error) return <Badge variant="destructive">Failed</Badge>
  if (b.timed_out) return <Badge variant="destructive">Timed out</Badge>
  if (b.symbols_failed) return <Badge variant="warning">Symbols failed</Badge>
  if (b.stale) return <Badge variant="warning">Out of date</Badge>
  return <Badge variant="success">Current</Badge>
}

/** What a change to many did, repository by repository. */
function Outcome({ title, result, onClose }: { title: string; result: BatchResult; onClose: () => void }) {
  const ok = result.results.filter((r) => r.ok).length
  const failed = result.results.length - ok
  return (
    <>
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        <DialogDescription>
          {failed === 0 ? `Done for all ${noun(ok)}.` : `Done for ${ok.toLocaleString()} of ${noun(result.results.length)}; ${failed.toLocaleString()} could not be changed, and each says why.`}
        </DialogDescription>
      </DialogHeader>
      <ul className="grid max-h-[55vh] gap-1 overflow-y-auto pr-1 text-sm" aria-label="Outcome for each repository">
        {[...result.results]
          .sort((a, b) => Number(a.ok) - Number(b.ok))
          .map((r) => (
            <li key={r.gitlab_id} className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-2 rounded-md border px-2.5 py-1.5">
              {r.ok ? <CircleCheck className="mt-0.5 size-4 text-success-ink" aria-label="Done" /> : <CircleX className="mt-0.5 size-4 text-destructive-ink" aria-label="Not done" />}
              <span className="grid gap-0.5">
                <span className="font-medium [overflow-wrap:anywhere]">{r.repo ?? `#${r.gitlab_id}`}</span>
                <span className="text-muted-foreground">{r.message}</span>
              </span>
            </li>
          ))}
      </ul>
      <DialogFooter>
        <Button onClick={onClose}>Done</Button>
      </DialogFooter>
    </>
  )
}

/** Branches (names or patterns) added to many repositories at once. */
function AddBranchesForm({ rows, pending, error, onCancel, onSubmit }: { rows: RepoRow[]; pending: boolean; error: string | null; onCancel: () => void; onSubmit: (branches: string[]) => void }) {
  const [text, setText] = useState('')
  const branches = text.split(/[\s,]+/).filter(Boolean)
  return (
    <>
      <DialogHeader>
        <DialogTitle>Add branches to {noun(rows.length)}</DialogTitle>
        <DialogDescription>{names(rows)}. Each keeps its own branches; these are indexed too, from the next run, where they exist.</DialogDescription>
      </DialogHeader>
      <form
        className="grid gap-4"
        onSubmit={(e) => {
          e.preventDefault()
          onSubmit(branches)
        }}
      >
        {error && <Alert variant="destructive">{error}</Alert>}
        <Field label="Branches or patterns" hint="Space separated: develop release/*">
          <Input className="font-mono" autoComplete="off" placeholder="release/*" required value={text} onChange={(e) => setText(e.target.value)} />
        </Field>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" loading={pending} disabled={branches.length === 0}>
            Add to {noun(rows.length)}
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}

/** A repository's branches as GitLab has them now (with their latest commits): which are indexed, and patterns for the rest. */
function BranchesForm({ repo, onClose, onSaved }: { repo: RepoRow; onClose: () => void; onSaved: () => Promise<unknown> }) {
  const remote = useQuery({ queryKey: ['admin', 'argus', 'branches', repo.gitlab_id], queryFn: ({ signal }) => api<RemoteBranch[]>(`/api/admin/argus/repos/${repo.gitlab_id}/branches`, { signal }) })
  const names = new Set(remote.data?.map((b) => b.name) ?? [])
  const [picked, setPicked] = useState<string[] | null>(null)
  const [patterns, setPatterns] = useState<string | null>(null)
  // Until GitLab answers, what is saved: names it lists are ticks, the rest patterns.
  const chosen = picked ?? repo.branches.filter((b) => names.has(b))
  const globs = patterns ?? repo.branches.filter((b) => !names.has(b)).join(' ')
  const [error, setError] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const save = useMutation({
    mutationFn: () => {
      const branches = [...chosen, ...globs.split(/[\s,]+/).filter(Boolean)]
      return api(`/api/admin/argus/repos/${repo.gitlab_id}`, { method: 'PATCH', body: { branches } })
    },
    onSuccess: async () => {
      await onSaved()
      toast.success(`Branches of ${repo.repo} saved`, { description: 'Indexed at the next run, or press Update.' })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const shown = (remote.data ?? []).filter((b) => b.name.toLowerCase().includes(filter.toLowerCase()))
  return (
    <>
      <DialogHeader>
        <DialogTitle>Branches of {repo.repo}</DialogTitle>
        <DialogDescription>The default branch is always indexed. Tick others to index them too; each is a full index of its own.</DialogDescription>
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
        {remote.error && <Alert variant="warning">{errorMessage(remote.error)}</Alert>}
        {remote.isPending ? (
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Asking GitLab for the branches…
          </p>
        ) : (
          remote.data && (
            <>
              {remote.data.length > 8 && <Input type="search" placeholder="Find a branch" aria-label="Find a branch" value={filter} onChange={(e) => setFilter(e.target.value)} className="h-8" />}
              <ul className="grid max-h-80 gap-1 overflow-y-auto" aria-label="Branches">
                {shown.map((b) => (
                  <li key={b.name}>
                    <label
                      aria-label={b.name}
                      className={cn('flex cursor-pointer items-start gap-2.5 rounded-md border px-2.5 py-2 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary/5 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring', b.default && 'cursor-default')}
                    >
                      <input
                        type="checkbox"
                        className="mt-0.5 size-3.5 shrink-0 accent-primary outline-none"
                        checked={b.default || chosen.includes(b.name)}
                        disabled={b.default}
                        onChange={(e) => setPicked(e.target.checked ? [...chosen, b.name] : chosen.filter((x) => x !== b.name))}
                      />
                      <span className="grid min-w-0 gap-0.5">
                        <span className="flex flex-wrap items-center gap-1.5">
                          <span className="font-mono">{b.name}</span>
                          {b.default && <Badge variant="secondary">default</Badge>}
                          {b.protected && <Badge variant="outline">protected</Badge>}
                        </span>
                        <span className="truncate text-xs text-muted-foreground">
                          <span className="font-mono">{short(b.sha)}</span> {b.message}
                          {b.committed_at ? ` · ${new Date(b.committed_at).toLocaleDateString()}` : ''}
                        </span>
                      </span>
                    </label>
                  </li>
                ))}
              </ul>
            </>
          )
        )}
        <Field label="Patterns" hint="For branches to come too: release/* and the like, space separated.">
          <Input className="font-mono" autoComplete="off" placeholder="release/*" value={globs} onChange={(e) => setPatterns(e.target.value)} />
        </Field>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={save.isPending}>
            Save
          </Button>
        </DialogFooter>
      </form>
    </>
  )
}
