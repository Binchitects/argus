import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { GitBranch, Loader2, RefreshCw, RotateCw } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, selectColumn, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { agoSeconds } from '@/lib/format'
import { cn } from '@/lib/utils'
import { repoProgress, type IndexProgress } from './argus-progress'

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

export interface RepoRow {
  gitlab_id: number
  repo: string
  default_branch: string
  included: boolean
  /** Branches indexed besides the default one: names or globs. */
  branches: string[]
  seen_at: number | null
  indexed: IndexedBranch[]
}

interface ReposView {
  configured?: false
  new_repos: 'include' | 'exclude'
  global_branches: string[]
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

const short = (sha: string | null) => (sha ? sha.slice(0, 8) : '')

/**
 * Admin → Indexing → Repositories: what GitLab lists, in or out of the index, the
 * branches of each (and their commits), and bringing one up to date now.
 */
export function RepositoriesCard({ running, progress, pending, gitlabUrl }: { running: boolean; progress: IndexProgress | null; pending: string[]; gitlabUrl: string | null }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const repos = useQuery({
    queryKey: ['admin', 'argus', 'repos'],
    queryFn: ({ signal }) => api<ReposView>('/api/admin/argus/repos', { signal }),
    refetchInterval: running ? 5000 : 60_000,
  })
  const [branchesOf, setBranchesOf] = useState<RepoRow | null>(null)
  const changed = () =>
    Promise.all([queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'repos'] }), queryClient.invalidateQueries({ queryKey: ['admin', 'argus', 'status'] })])
  const choose = useMutation({
    mutationFn: ({ rows, included }: { rows: RepoRow[]; included: boolean }) =>
      Promise.all(rows.map((r) => api<{ deferred: boolean }>(`/api/admin/argus/repos/${r.gitlab_id}`, { method: 'PATCH', body: { included } }))),
    onSuccess: async (res, { rows, included }) => {
      await changed()
      const what = rows.length === 1 ? rows[0]!.repo : `${rows.length} repositories`
      if (included) toast.success(`${what} will be indexed`, { description: 'At the next pass, or press Update.' })
      else toast.success(`${what} left out of the index`, { description: res.some((r) => r.deferred) ? 'Removed once the pass running now ends.' : 'Its files and symbols are removed.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const update = useMutation({
    mutationFn: (r: RepoRow) => api<{ status: string; queued: number }>('/api/admin/argus/index', { body: { repo: r.repo } }),
    onSuccess: async (res, r) => {
      await changed()
      if (res.status === 'started') toast.success(`Updating ${r.repo}`, { description: 'From its latest commits: only what changed is read again.' })
      else if (res.status === 'already_queued') toast(`${r.repo} is already waiting its turn`)
      else toast.success(`${r.repo} is queued`, { description: 'It is updated when the pass running now ends.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const discover = useMutation({
    mutationFn: () => api<ReposView>('/api/admin/argus/repos/discover', { method: 'POST' }),
    onSuccess: async (v) => {
      await changed()
      toast.success(`GitLab lists ${v.repos.length} repositories`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const policy = useMutation({
    mutationFn: (newRepos: string) => api('/api/admin/argus/repos/settings', { method: 'PUT', body: { newRepos } }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const leaveOut = async (rows: RepoRow[]) => {
    const what = rows.length === 1 ? rows[0]!.repo : `${rows.length} repositories`
    if (await confirm({ title: `Leave ${what} out of the index?`, description: 'Their files and symbols are removed, and answers no longer cover them. Choose them again to index them anew.', confirm: 'Leave out', destructive: true }))
      choose.mutate({ rows, included: false })
  }

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
      accessorKey: 'repo',
      header: ({ column }) => <SortHeader column={column} title="Repository" />,
      cell: ({ row: { original: r } }) => (
        <div className="grid min-w-0 gap-1">
          <span className="font-medium [overflow-wrap:anywhere]">{r.repo}</span>
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
      id: 'index',
      header: 'Index',
      accessorFn: (r) => r.indexed.map((b) => `${b.branch} ${b.sha ?? ''} ${b.message ?? ''}`).join(' '),
      cell: ({ row: { original: r } }) => <IndexState repo={r} now={repoProgress(r.repo, running, progress, pending)} gitlabUrl={gitlabUrl} />,
    },
    {
      id: 'actions',
      enableSorting: false,
      header: () => <span className="sr-only">Actions</span>,
      cell: ({ row: { original: r } }) => (
        <Button variant="outline" size="sm" disabled={!r.included || update.isPending} onClick={() => update.mutate(r)} aria-label={`Update the index of ${r.repo}`}>
          <RotateCw /> Update
        </Button>
      ),
    },
  ]

  const view = repos.data
  return (
    <Card aria-label="Repositories">
      <CardHeader className="flex flex-row flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <CardTitle>Repositories</CardTitle>
          <CardDescription>
            What GitLab lists, and which are indexed: each at its default branch, and the branches you add. Update brings one up to date from its latest commits now
            (only what changed is read again).
          </CardDescription>
        </div>
        <Button variant="outline" size="sm" onClick={() => discover.mutate()} loading={discover.isPending}>
          <RefreshCw /> Refresh from GitLab
        </Button>
      </CardHeader>
      {/* One column as wide as the card: the table scrolls inside it rather than widening the page. */}
      <CardContent className="grid grid-cols-[minmax(0,1fr)] gap-4">
        {repos.error && <Alert variant="destructive">{errorMessage(repos.error)}</Alert>}
        {view && view.global_branches.length > 0 && (
          <p className="text-xs text-muted-foreground">
            Every repository also has these branches indexed (ARGUS_INDEX_BRANCHES): <span className="font-mono">{view.global_branches.join(' ')}</span>
          </p>
        )}
        <DataTable
          columns={columns}
          data={view?.repos}
          loading={repos.isPending}
          noun="repositories"
          searchPlaceholder="Find a repository or a commit…"
          getRowId={(r) => String(r.gitlab_id)}
          initialSorting={[{ id: 'repo', desc: false }]}
          empty={<p className="text-sm text-muted-foreground">No repositories yet: Refresh from GitLab lists them, and so does every index pass.</p>}
          toolbar={
            view && (
              <div className="flex items-center gap-2 text-sm">
                <span className="text-muted-foreground">New repositories</span>
                <Select value={view.new_repos} onValueChange={(v) => policy.mutate(v)}>
                  <SelectTrigger size="sm" className="w-44" aria-label="New repositories">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="include">Index them</SelectItem>
                    <SelectItem value="exclude">Leave them out</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            )
          }
          bulk={(selected) => (
            <>
              <Button size="sm" variant="outline" onClick={() => choose.mutate({ rows: selected, included: true })}>
                Index them
              </Button>
              <Button size="sm" variant="outline" onClick={() => void leaveOut(selected)}>
                Leave them out
              </Button>
            </>
          )}
        />
      </CardContent>
      <Dialog open={branchesOf !== null} onOpenChange={(o) => !o && setBranchesOf(null)}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {branchesOf && <BranchesForm key={branchesOf.gitlab_id} repo={branchesOf} onClose={() => setBranchesOf(null)} onSaved={changed} />}
        </DialogContent>
      </Dialog>
    </Card>
  )
}

/** Each indexed branch of a repository: its commit (hash and message), when, how the last run went; or where the run is with it. */
function IndexState({ repo, now, gitlabUrl }: { repo: RepoRow; now: ReturnType<typeof repoProgress>; gitlabUrl: string | null }) {
  return (
    <div className="grid min-w-0 gap-1.5 text-xs">
      {now?.state === 'indexing' && (
        <div className="grid gap-1">
          <span className="flex items-center gap-1.5 text-foreground">
            <Loader2 className="size-3.5 animate-spin" aria-hidden="true" /> Indexing <span className="font-mono">{now.branch}</span>
            {now.percent !== null && <span className="tabular-nums">{now.percent}%</span>}
          </span>
          <progress value={now.percent ?? 0} max={100} aria-label={`${repo.repo}: indexing`} className="h-1.5 w-full max-w-56 appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary" />
        </div>
      )}
      {now?.state === 'queued' && <Badge variant="outline">Queued</Badge>}
      {repo.indexed.length === 0 && !now && <span className="text-muted-foreground">{repo.included ? 'Not indexed yet: the next pass, or Update.' : 'Not indexed.'}</span>}
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
            <State b={b} />
          </span>
          {b.message && (
            <span dir="auto" className="truncate text-muted-foreground" title={b.message}>
              {b.message}
            </span>
          )}
          <span className="text-muted-foreground">
            {b.committed_at ? `committed ${agoSeconds(b.committed_at)} · ` : ''}checked {agoSeconds(b.last_run_at)} · {b.files.toLocaleString()} files, {b.symbols.toLocaleString()} symbols
          </span>
          {b.error && <span className="text-destructive-ink [overflow-wrap:anywhere]">{b.error}</span>}
        </div>
      ))}
    </div>
  )
}

function State({ b }: { b: IndexedBranch }) {
  if (b.error) return <Badge variant="destructive">Failed</Badge>
  if (b.timed_out) return <Badge variant="destructive">Timed out</Badge>
  if (b.symbols_failed) return <Badge variant="warning">Symbols failed</Badge>
  if (b.stale) return <Badge variant="warning">Out of date</Badge>
  return <Badge variant="success">Current</Badge>
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
      toast.success(`Branches of ${repo.repo} saved`, { description: 'Indexed at the next pass, or press Update.' })
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
