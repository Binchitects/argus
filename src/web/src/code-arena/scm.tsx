import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, GitBranch, GitCommitHorizontal, Minus, Plus, RefreshCw, Undo2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import {
  gitBranchesQuery,
  gitCommit,
  gitDiscard,
  gitLogQuery,
  gitStage,
  gitStatusQuery,
  gitSwitch,
  gitUnstage,
  nameOf,
  parentOf,
  type GitFile,
  type GitStatus,
} from './ide-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const letters: Record<string, string> = { M: 'changed', A: 'added', D: 'deleted', R: 'renamed', C: 'copied', '?': 'not in git', U: 'in conflict', T: 'type changed' }
const tones: Record<string, string> = { M: 'text-warning-ink', A: 'text-success-ink', '?': 'text-success-ink', D: 'text-destructive-ink', U: 'text-destructive-ink' }

/**
 * Source Control: what changed since the last commit, staged (in the next commit) and not; each file opens as it was
 * then and is now; + stages, − unstages, ↺ puts it back; the message and Commit (all of it when nothing is staged);
 * the branch, to switch, and the last commits.
 */
export function SourceControlPanel() {
  const editor = useEditor()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const status = useQuery(gitStatusQuery)
  const log = useQuery(gitLogQuery)
  const branches = useQuery({ ...gitBranchesQuery, enabled: !!status.data?.repository })
  const [message, setMessage] = useState('')
  const settle = (s: GitStatus) => {
    queryClient.setQueryData(gitStatusQuery.queryKey, s)
    // The files in the editor and the explorer as they are now.
    void editor.refresh()
    void queryClient.invalidateQueries({ queryKey: ['code', 'files'] })
  }
  const act = useMutation({
    mutationFn: ({ op, paths }: { op: 'stage' | 'unstage' | 'discard'; paths: string[] }) => (op === 'stage' ? gitStage(paths) : op === 'unstage' ? gitUnstage(paths) : gitDiscard(paths)),
    onSuccess: settle,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const files = status.data?.files ?? []
  const staged = files.filter((f) => f.staged)
  const changed = files.filter((f) => f.changed)
  const commit = useMutation({
    mutationFn: () => gitCommit(message, staged.length === 0),
    onSuccess: (r) => {
      settle(r.status)
      setMessage('')
      toast.success(`Committed ${r.hash}.`)
      void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const switchTo = useMutation({
    mutationFn: gitSwitch,
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
  const busy = act.isPending || commit.isPending || switchTo.isPending

  const row = (f: GitFile, list: 'staged' | 'changed') => {
    const letter = (list === 'staged' ? f.staged : f.changed) ?? ''
    return (
      <li key={`${list}:${f.path}`} className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
        <button
          type="button"
          onClick={() => void editor.openDiff(f.path, 'git')}
          title={`${f.path}: as the last commit has it, and now`}
          className="flex h-full min-w-0 flex-1 items-center gap-1.5 pl-3 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
        >
          <FileIcon path={f.path} />
          <span className={cn('truncate', letter === 'D' && 'line-through')}>{nameOf(f.path)}</span>
          <span className="min-w-0 truncate text-xs text-muted-foreground">{parentOf(f.path)}</span>
          <span className="sr-only">, {letters[letter] ?? letter}</span>
        </button>
        <span className="hidden shrink-0 items-center group-hover:flex group-focus-within:flex">
          {list === 'changed' && (
            <Tooltip content="Discard the changes">
              <button type="button" aria-label={`Discard ${f.path}`} onClick={() => void discard([f.path])} disabled={busy} className={action}>
                <Undo2 />
              </button>
            </Tooltip>
          )}
          <Tooltip content={list === 'staged' ? 'Unstage' : 'Stage'}>
            <button type="button" aria-label={`${list === 'staged' ? 'Unstage' : 'Stage'} ${f.path}`} onClick={() => act.mutate({ op: list === 'staged' ? 'unstage' : 'stage', paths: [f.path] })} disabled={busy} className={action}>
              {list === 'staged' ? <Minus /> : <Plus />}
            </button>
          </Tooltip>
        </span>
        <span className={cn('ml-1 w-3 shrink-0 text-center font-mono text-[0.6875rem] font-semibold', tones[letter] ?? 'text-muted-foreground')} aria-hidden="true">
          {letter === '?' ? 'U' : letter}
        </span>
      </li>
    )
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Source control</h2>
        <Tooltip content="Read git's state again">
          <button type="button" aria-label="Refresh source control" onClick={() => void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })} className={action}>
            <RefreshCw />
          </button>
        </Tooltip>
        <PartHelp part="git" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        {status.isPending && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
        {status.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(status.error)}</p>}
        {status.data && !status.data.repository && <p className="px-3 py-2 text-muted-foreground">This folder is in no git repository. `git init` in a terminal makes one.</p>}
        {status.data?.repository && (
          <>
            <form
              className="grid gap-1.5 px-3 pb-2"
              onSubmit={(e) => {
                e.preventDefault()
                if (message.trim()) commit.mutate()
              }}
            >
              <Textarea
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && message.trim()) {
                    e.preventDefault()
                    commit.mutate()
                  }
                }}
                rows={2}
                placeholder={status.data.branch ? `Message (Ctrl+Enter commits on ${status.data.branch})` : 'Message (Ctrl+Enter commits)'}
                aria-label="Commit message"
                className="min-h-14 text-sm"
              />
              <Button type="submit" size="sm" disabled={busy || !message.trim() || files.length === 0}>
                <Check /> {staged.length > 0 ? `Commit ${staged.length} staged` : 'Commit all'}
              </Button>
            </form>
            <section aria-label="Staged changes">
              <h3 className="flex h-[1.375rem] items-center px-3 text-xs font-semibold text-muted-foreground">
                Staged <span className="ml-1 tabular-nums">({staged.length})</span>
                {staged.length > 0 && (
                  <button type="button" onClick={() => act.mutate({ op: 'unstage', paths: staged.map((f) => f.path) })} disabled={busy} className="ml-auto text-xs font-normal hover:text-foreground">
                    Unstage all
                  </button>
                )}
              </h3>
              <ul aria-label="Staged files">{staged.map((f) => row(f, 'staged'))}</ul>
            </section>
            <section aria-label="Changes">
              <h3 className="flex h-[1.375rem] items-center px-3 text-xs font-semibold text-muted-foreground">
                Changes <span className="ml-1 tabular-nums">({changed.length})</span>
                {changed.length > 0 && (
                  <button type="button" onClick={() => act.mutate({ op: 'stage', paths: changed.map((f) => f.path) })} disabled={busy} className="ml-auto text-xs font-normal hover:text-foreground">
                    Stage all
                  </button>
                )}
              </h3>
              <ul aria-label="Changed files">{changed.map((f) => row(f, 'changed'))}</ul>
              {files.length === 0 && <p className="px-3 py-1 text-xs text-muted-foreground">Nothing changed since the last commit.</p>}
            </section>
            <section aria-label="Branch" className="mt-3 grid gap-1 px-3">
              <h3 className="flex items-center gap-1 text-xs font-semibold text-muted-foreground">
                <GitBranch className="size-3.5" aria-hidden="true" /> Branch
                {(status.data.ahead ?? 0) + (status.data.behind ?? 0) > 0 && (
                  <span className="ml-auto font-normal tabular-nums">
                    ↑{status.data.ahead} ↓{status.data.behind}
                  </span>
                )}
              </h3>
              {branches.data && status.data.branch && (
                <Select value={status.data.branch} onValueChange={(b) => b !== status.data?.branch && switchTo.mutate(b)} disabled={busy}>
                  <SelectTrigger size="sm" aria-label="Switch branch" className="h-7 text-xs">
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
              )}
            </section>
            <section aria-label="Last commits" className="mt-3">
              <h3 className="flex h-[1.375rem] items-center gap-1 px-3 text-xs font-semibold text-muted-foreground">
                <GitCommitHorizontal className="size-3.5" aria-hidden="true" /> Last commits
              </h3>
              <ul aria-label="Commits">
                {(log.data ?? []).slice(0, 15).map((c) => (
                  <li key={c.hash} className="flex min-w-0 items-baseline gap-2 px-3 py-0.5" title={`${c.hash} ${c.subject} — ${c.author}, ${new Date(c.at).toLocaleString()}`}>
                    <code className="shrink-0 font-mono text-[0.6875rem] text-muted-foreground">{c.hash}</code>
                    <span className="min-w-0 truncate">{c.subject}</span>
                  </li>
                ))}
              </ul>
            </section>
          </>
        )}
      </div>
    </div>
  )
}
