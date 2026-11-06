import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Check, ChevronDown, ChevronRight, CloudUpload, Ellipsis, GitBranch, GitCommitHorizontal, Minus, Plus, RefreshCw, Undo2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { folderKeys, rowKeys, treeIndent, useChangeView } from './change-folds'
import { ChangeFiles, FoldGroup, ViewTools } from './change-tree'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { useGit } from './git-actions'
import { PartHelp } from './help'
import {
  gitBranchesQueryOf,
  gitCommit,
  gitDiscard,
  gitLogQueryOf,
  gitStage,
  gitPush,
  gitStatusQueryOf,
  gitSwitch,
  gitSync,
  gitUnstage,
  nameOf,
  parentOf,
  gitCommitFiles,
  type GitCommit,
  type GitFile,
  type GitRepository,
  type GitStatus,
} from './ide-api'
import type { Side } from './compare'
import { useRepositories } from './repositories'

/** A commit of the history: opened, the files it changed; each opens its change (the commit's parent ↔ the commit). */
function CommitRow({ commit: c, repo }: { commit: GitCommit; repo: string }) {
  const editor = useEditor()
  const [open, setOpen] = useState(false)
  const files = useQuery({ queryKey: ['code', 'git', 'commit', repo, c.hash], queryFn: () => gitCommitFiles(c.hash, repo), enabled: open, staleTime: Infinity })
  return (
    <li data-node>
      <button
        type="button"
        data-row
        data-head
        onKeyDown={rowKeys}
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        title={`${c.hash} ${c.subject} — ${c.author}, ${new Date(c.at).toLocaleString()}`}
        className="flex w-full min-w-0 items-center gap-1 py-0.5 pr-3 pl-3 text-left outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
      >
        <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
        <code className="shrink-0 font-mono text-[0.6875rem] text-muted-foreground">{c.hash.slice(0, 7)}</code>
        <span className="min-w-0 truncate">{c.subject}</span>
      </button>
      {open && (
        <ul aria-label={`Files of ${c.hash.slice(0, 7)}`} className="pl-5">
          {files.isPending && <Skeleton className="mx-3 my-1 h-3.5" />}
          {files.error && <li className="px-3 text-xs text-muted-foreground">{errorMessage(files.error)}</li>}
          {files.data?.files.map((f) => (
            <li key={f.path}>
              <button
                type="button"
                data-row
                onKeyDown={rowKeys}
                onClick={() => {
                  const root = (files.data?.parents.length ?? 0) === 0
                  const left: Side = f.status === 'A' || root ? { kind: 'empty', path: f.path } : { kind: 'git', path: f.from ?? f.path, rev: `${files.data!.hash}^` }
                  const right: Side = f.status === 'D' ? { kind: 'empty', path: f.path } : { kind: 'git', path: f.path, rev: files.data!.hash }
                  void editor.compare(left, right)
                }}
                className="flex h-[1.375rem] w-full min-w-0 items-center gap-1.5 px-3 text-left outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
              >
                <FileIcon path={f.path} />
                <span className={cn('truncate', f.status === 'D' && 'line-through')}>{nameOf(f.path)}</span>
                <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(f.path)}</span>
                <span className={cn('ml-auto w-3 shrink-0 text-center font-mono text-[0.6875rem] font-semibold', tones[f.status] ?? 'text-muted-foreground')}>{f.status}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const letters: Record<string, string> = { M: 'changed', A: 'added', D: 'deleted', R: 'renamed', C: 'copied', '?': 'not in git', U: 'in conflict', T: 'type changed' }
const tones: Record<string, string> = { M: 'text-warning-ink', A: 'text-success-ink', '?': 'text-success-ink', D: 'text-destructive-ink', U: 'text-destructive-ink' }

/**
 * Source Control, as VS Code's: the folder's repository, or, when there are several (one in a folder of it, an added
 * folder's: as git.autoRepositoryDetection finds them, repositories.ts), a section for each, each folding, with its
 * branch, its own commit box, staged changes and changes. One repository has no section of its own.
 */
export function SourceControlPanel() {
  const queryClient = useQueryClient()
  const { repositories } = useRepositories()
  // Until they are listed (or when they cannot be), the folder's own: the one there nearly always is.
  const list = repositories ?? []
  if (list.length <= 1) return <Repository repo={list[0]?.path ?? '.'} />
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Source control</h2>
        <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{list.length} repositories</span>
        <Tooltip content="Read git's state again">
          <button type="button" aria-label="Refresh source control" onClick={() => void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })} className={action}>
            <RefreshCw />
          </button>
        </Tooltip>
        <PartHelp part="git" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]" data-tree>
        {list.map((r) => (
          <Repository key={r.path} repo={r.path} section={r} />
        ))}
      </div>
    </div>
  )
}

/**
 * A repository in Source Control: what changed since the last commit, in conflict, staged (in the next commit) and not;
 * each file opens as it was then and is now (one in conflict, as it is); + stages, − unstages, ↺ puts it back; the
 * message and Commit (all of it when nothing is staged); the branch, to switch, and the last commits. Each group folds,
 * its files a list or a tree of their folders (change-tree.tsx). Alone, it is the whole view; one of several, a
 * `section` that folds, its name and branch on its row, its own tools there.
 */
function Repository({ repo, section }: { repo: string; section?: GitRepository }) {
  const editor = useEditor()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const status = useQuery(gitStatusQueryOf(repo))
  const log = useQuery(gitLogQueryOf(repo))
  const branches = useQuery({ ...gitBranchesQueryOf(repo), enabled: !!status.data?.repository })
  const [message, setMessage] = useState('')
  const settle = (s: GitStatus) => {
    queryClient.setQueryData(gitStatusQueryOf(repo).queryKey, s)
    // The files in the editor and the explorer as they are now.
    void editor.refresh()
    void queryClient.invalidateQueries({ queryKey: ['code', 'files'] })
  }
  const act = useMutation({
    mutationFn: ({ op, paths }: { op: 'stage' | 'unstage' | 'discard'; paths: string[] }) => (op === 'stage' ? gitStage(paths, repo) : op === 'unstage' ? gitUnstage(paths, repo) : gitDiscard(paths, repo)),
    onSuccess: settle,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const files = status.data?.files ?? []
  // A file in conflict is in Merge Changes alone, as VS Code has it.
  const merge = files.filter((f) => f.conflict)
  const staged = files.filter((f) => f.staged && !f.conflict)
  const changed = files.filter((f) => f.changed && !f.conflict)
  const pathOf = (f: GitFile) => f.path
  const groups = [['merge', merge], ['staged', staged], ['changes', changed]] as const
  // A section's own fold too; each repository's folds are kept apart (the folder's own as they always were).
  const keys = [...(section ? ['repository'] : []), 'merge', 'staged', 'changes', 'branch', 'commits', ...groups.flatMap(([g, list]) => folderKeys(g, list, pathOf))]
  const view = useChangeView(repo === '.' ? 'scm' : `scm:${repo}`, keys)
  const all = useGit()
  const git = { ...all.in(repo), busy: all.busy, rebase: all.rebase }
  /** Commit (Amend: into the last commit), then, as asked, push or sync. */
  const commit = useMutation({
    mutationFn: async ({ amend = false, then }: { amend?: boolean; then?: 'push' | 'sync' } = {}) => {
      const done = await gitCommit(message, staged.length === 0, amend, repo)
      if (then === 'push') await gitPush(false, repo)
      if (then === 'sync') await gitSync(git.rebase(), repo)
      return { ...done, amend, then }
    },
    onSuccess: (r) => {
      settle(r.status)
      setMessage('')
      toast.success(`${r.amend ? 'Amended' : 'Committed'} ${r.hash}${r.then === 'push' ? ', and pushed' : r.then === 'sync' ? ', and synced' : ''}.`)
      void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
    },
    onError: (e) => {
      toast.error(errorMessage(e))
      void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
    },
  })
  const switchTo = useMutation({
    mutationFn: (branch: string) => gitSwitch(branch, 'local', repo),
    onSuccess: (s) => {
      settle(s)
      void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const discard = async (paths: string[]) => {
    const one = paths.length === 1 ? nameOf(paths[0]!) : `${paths.length} files`
    const ok = await confirm({
      title: `Discard the changes to ${one}?`,
      description: 'They go back to how the last commit has them (a file git does not know is deleted). This cannot be undone.',
      confirm: 'Discard',
      destructive: true,
    })
    if (ok) act.mutate({ op: 'discard', paths })
  }
  const busy = act.isPending || commit.isPending || switchTo.isPending || git.busy !== null
  const ahead = status.data?.ahead ?? 0
  const behind = status.data?.behind ?? 0
  // No upstream (ahead and behind unknown): the branch is to be published.
  const unpublished = !!status.data?.branch && status.data.ahead == null

  /** A group's actions on these files (all of it, or a folder's): stage, unstage, put back. */
  const actions = (list: 'merge' | 'staged' | 'changed', paths: string[], what: string) => (
    <>
      {list === 'changed' && (
        <Tooltip content="Discard the changes">
          <button type="button" aria-label={`Discard ${what}`} onClick={() => void discard(paths)} disabled={busy} className={action}>
            <Undo2 />
          </button>
        </Tooltip>
      )}
      <Tooltip content={list === 'staged' ? 'Unstage' : 'Stage'}>
        <button type="button" aria-label={`${list === 'staged' ? 'Unstage' : 'Stage'} ${what}`} onClick={() => act.mutate({ op: list === 'staged' ? 'unstage' : 'stage', paths })} disabled={busy} className={action}>
          {list === 'staged' ? <Minus /> : <Plus />}
        </button>
      </Tooltip>
    </>
  )

  /** A file of a group: in the list with its folder beside it (depth null), or in the tree at its depth. */
  const row = (f: GitFile, list: 'merge' | 'staged' | 'changed', depth: number | null) => {
    const letter = list === 'merge' ? '!' : ((list === 'staged' ? f.staged : f.changed) ?? '')
    return (
      <li key={`${list}:${f.path}`} className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
        <button
          type="button"
          data-row
          onKeyDown={rowKeys}
          onClick={() => void (list === 'merge' ? editor.open(f.path) : editor.openDiff(f.path, list === 'staged' ? 'staged' : 'git'))}
          title={list === 'merge' ? `${f.path}: in conflict; staging it settles it` : list === 'staged' ? `${f.path}: as the last commit has it, and as staged` : `${f.path}: as staged (or committed), and now`}
          style={depth === null ? undefined : treeIndent(depth)}
          className={cn('flex h-full min-w-0 flex-1 items-center gap-1.5 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset', depth === null && 'pl-3')}
        >
          <FileIcon path={f.path} />
          <span className={cn('truncate', letter === 'D' && 'line-through')}>{nameOf(f.path)}</span>
          {depth === null && <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(f.path)}</span>}
          <span className="sr-only">, {list === 'merge' ? letters.U : (letters[letter] ?? letter)}</span>
        </button>
        <span className="hidden shrink-0 items-center group-hover:flex group-focus-within:flex">{actions(list, [f.path], f.path)}</span>
        <span className={cn('ml-1 w-3 shrink-0 text-center font-mono text-[0.6875rem] font-semibold', list === 'merge' ? tones.U : (tones[letter] ?? 'text-muted-foreground'))} aria-hidden="true">
          {letter === '?' ? 'U' : letter}
        </span>
      </li>
    )
  }

  const more = status.data?.repository && (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button type="button" aria-label={section ? `More git actions for ${section.name}` : 'More git actions'} className={action} disabled={busy}>
          <Ellipsis />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={() => void git.pull()}>Pull</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void git.push()}>{unpublished ? 'Publish Branch' : 'Push'}</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void git.fetch()}>Fetch</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void git.sync()}>Sync</DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void git.checkout()}>Checkout to…</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void git.createBranch()}>Create Branch…</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void git.renameBranch()}>Rename Branch…</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void git.deleteBranch()}>Delete Branch…</DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void git.undoCommit()}>Undo Last Commit</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
  const refresh = (
    <Tooltip content="Read git's state again">
      <button type="button" aria-label={section ? `Refresh ${section.name}` : 'Refresh source control'} onClick={() => void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })} className={action}>
        <RefreshCw />
      </button>
    </Tooltip>
  )
  const parts = (
    <>
      {status.isPending && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
      {status.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(status.error)}</p>}
      {status.data && !status.data.repository && <p className="px-3 py-2 text-muted-foreground">This folder is in no git repository. `git init` in a terminal makes one.</p>}
      {status.data?.repository && (
        <>
          <form
            className="grid gap-1.5 px-3 pb-2"
            onSubmit={(e) => {
              e.preventDefault()
              if (message.trim()) commit.mutate({})
            }}
          >
            <Textarea
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && message.trim()) {
                  e.preventDefault()
                  commit.mutate({})
                }
              }}
              rows={2}
              placeholder={status.data.branch ? `Message (Ctrl+Enter commits on ${status.data.branch})` : 'Message (Ctrl+Enter commits)'}
              aria-label={section ? `Commit message for ${section.name}` : 'Commit message'}
              className="min-h-14 text-sm"
            />
            <div className="flex">
              <Button type="submit" size="sm" disabled={busy || !message.trim() || files.length === 0} className="min-w-0 flex-1 rounded-r-none">
                <Check /> {staged.length > 0 ? `Commit ${staged.length} staged` : 'Commit all'}
              </Button>
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button type="button" size="sm" aria-label="Commit, and more" disabled={busy} className="rounded-l-none border-l border-primary-foreground/30 px-1.5">
                    <ChevronDown />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuItem disabled={!message.trim() || files.length === 0} onSelect={() => commit.mutate({})}>Commit</DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => commit.mutate({ amend: true })}>Commit (Amend)</DropdownMenuItem>
                  <DropdownMenuItem disabled={!message.trim() || files.length === 0} onSelect={() => commit.mutate({ then: 'push' })}>Commit &amp; Push</DropdownMenuItem>
                  <DropdownMenuItem disabled={!message.trim() || files.length === 0} onSelect={() => commit.mutate({ then: 'sync' })}>Commit &amp; Sync</DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </div>
            {(unpublished || ahead + behind > 0) && (
              <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => void (unpublished ? git.publish() : git.sync())}>
                {unpublished ? (
                  <>
                    <CloudUpload /> Publish Branch
                  </>
                ) : (
                  <>
                    <RefreshCw /> Sync Changes {behind > 0 && <><ArrowDown aria-hidden="true" />{behind}</>} {ahead > 0 && <><ArrowUp aria-hidden="true" />{ahead}</>}
                  </>
                )}
              </Button>
            )}
          </form>
          {merge.length > 0 && (
            <FoldGroup label="Merge changes" count={merge.length} open={!view.folded('merge')} onToggle={() => view.toggle('merge')} actions={actions('merge', merge.map(pathOf), 'all merge changes')}>
              <ChangeFiles label="Files in conflict" group="merge" items={merge} pathOf={pathOf} view={view} row={(f, depth) => row(f, 'merge', depth)} folderActions={(d, paths) => actions('merge', paths, d.path)} />
            </FoldGroup>
          )}
          <FoldGroup label="Staged changes" count={staged.length} open={!view.folded('staged')} onToggle={() => view.toggle('staged')} actions={staged.length > 0 && actions('staged', staged.map(pathOf), 'all')}>
            <ChangeFiles label="Staged files" group="staged" items={staged} pathOf={pathOf} view={view} row={(f, depth) => row(f, 'staged', depth)} folderActions={(d, paths) => actions('staged', paths, d.path)} />
          </FoldGroup>
          <FoldGroup label="Changes" count={changed.length} open={!view.folded('changes')} onToggle={() => view.toggle('changes')} actions={changed.length > 0 && actions('changed', changed.map(pathOf), 'all')}>
            <ChangeFiles label="Changed files" group="changes" items={changed} pathOf={pathOf} view={view} row={(f, depth) => row(f, 'changed', depth)} folderActions={(d, paths) => actions('changed', paths, d.path)} />
            {files.length === 0 && <p className="px-3 py-1 text-xs text-muted-foreground">Nothing changed since the last commit.</p>}
          </FoldGroup>
          <FoldGroup
            label="Branch"
            title={
              <span className="flex items-center gap-1">
                <GitBranch className="size-3.5" aria-hidden="true" /> Branch
              </span>
            }
            aside={
              (status.data.ahead ?? 0) + (status.data.behind ?? 0) > 0 && (
                <span className="ml-auto shrink-0 text-xs text-muted-foreground tabular-nums">
                  ↑{status.data.ahead} ↓{status.data.behind}
                </span>
              )
            }
            open={!view.folded('branch')}
            onToggle={() => view.toggle('branch')}
            className="mt-3"
          >
            {branches.data && status.data.branch && (
              <div className="px-3 pt-1">
                <Select value={status.data.branch} onValueChange={(b) => b !== status.data?.branch && switchTo.mutate(b)} disabled={busy}>
                  <SelectTrigger size="sm" aria-label="Switch branch" className="h-7 w-full text-xs">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {branches.data.map((b) => (
                      <SelectItem key={b.name} value={b.name}>
                        {b.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            )}
          </FoldGroup>
          <FoldGroup
            label="Last commits"
            title={
              <span className="flex items-center gap-1">
                <GitCommitHorizontal className="size-3.5" aria-hidden="true" /> Last commits
              </span>
            }
            open={!view.folded('commits')}
            onToggle={() => view.toggle('commits')}
            className="mt-3"
          >
            <ul aria-label="Commits">
              {(log.data ?? []).slice(0, 15).map((c) => (
                <CommitRow key={c.hash} commit={c} repo={repo} />
              ))}
            </ul>
          </FoldGroup>
        </>
      )}
    </>
  )
  if (section) {
    // One of several: its row, as VS Code's repository rows, the branch and the sync beside its name; what it holds below.
    const open = !view.folded('repository')
    return (
      <div data-node className="border-b border-sidebar-border/60 pb-1 last:border-b-0">
        <div className="group/head flex h-[1.625rem] items-center gap-0.5 pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
          <button
            type="button"
            data-row
            data-head
            onKeyDown={rowKeys}
            aria-expanded={open}
            aria-label={`Repository ${section.name}${status.data?.branch ? `, on ${status.data.branch}` : ''}, ${files.length} ${files.length === 1 ? 'change' : 'changes'}`}
            title={section.path === '.' ? 'The folder\'s own repository' : section.path}
            onClick={() => view.toggle('repository')}
            className="flex h-full min-w-0 flex-1 items-center gap-1 pl-1 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
          >
            <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
            <span className="truncate text-xs font-semibold">{section.name}</span>
            {status.data?.branch && (
              <span className="flex min-w-0 items-center gap-0.5 truncate text-xs text-muted-foreground">
                <GitBranch className="size-3 shrink-0" aria-hidden="true" />
                {status.data.branch}
                {ahead + behind > 0 && ` ↑${ahead} ↓${behind}`}
              </span>
            )}
          </button>
          <span className="hidden shrink-0 items-center group-focus-within/head:flex group-hover/head:flex">
            {status.data?.repository && <ViewTools view={view} keys={keys} />}
            {refresh}
            {more}
          </span>
          {files.length > 0 && (
            <span className="ml-1 min-w-4 shrink-0 rounded-full bg-muted px-1 text-center text-[0.6875rem] text-muted-foreground tabular-nums" aria-hidden="true">
              {files.length}
            </span>
          )}
        </div>
        {open && <div className="pt-1">{parts}</div>}
      </div>
    )
  }
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Source control</h2>
        {status.data?.repository && <ViewTools view={view} keys={keys} />}
        {refresh}
        {more}
        <PartHelp part="git" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]" data-tree>
        {parts}
      </div>
    </div>
  )
}
