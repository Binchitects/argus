import { useQuery } from '@tanstack/react-query'
import { CircleAlert, Info, Loader2, TriangleAlert } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { api, errorMessage } from '@/lib/api'
import { when } from '@/lib/format'
import { cn } from '@/lib/utils'
import { isWorking, progressPercent, progressWords, type RepoProgress } from './argus-progress'

export interface LogLine {
  /** The run it belongs to: when the run started, in unix milliseconds. */
  run: number
  /** Unix seconds. */
  at: number
  level: 'info' | 'warning' | 'error'
  text: string
}

interface RepoLog {
  repo: string
  lines: LogLine[]
  progress: RepoProgress | null
}

const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })

const levels = {
  info: { icon: Info, label: 'Info', className: 'text-muted-foreground' },
  warning: { icon: TriangleAlert, label: 'Warning', className: 'text-warning-ink' },
  error: { icon: CircleAlert, label: 'Error', className: 'text-destructive-ink' },
} as const

/** A repository's log: its last runs, newest first, each line a sentence with its time; live while it is indexed. */
export function RepoLogView({ gitlabId, repo }: { gitlabId: number; repo: string }) {
  const log = useQuery({
    queryKey: ['admin', 'argus', 'repo-log', gitlabId],
    queryFn: ({ signal }) => api<RepoLog>(`/api/admin/argus/repos/${gitlabId}/log?runs=5`, { signal }),
    refetchInterval: (q) => (q.state.data?.progress && q.state.data.progress.state !== 'done' && q.state.data.progress.state !== 'failed' ? 2000 : false),
  })
  const runs = new Map<number, LogLine[]>()
  for (const line of log.data?.lines ?? []) runs.set(line.run, [...(runs.get(line.run) ?? []), line])
  const newest = [...runs.entries()].sort(([a], [b]) => b - a)
  const p = log.data?.progress
  const percent = progressPercent(p)
  return (
    <>
      <DialogHeader>
        <DialogTitle>Log of {repo}</DialogTitle>
        <DialogDescription>What its last runs did, newest first, and what admins changed. Warnings and errors say what to do.</DialogDescription>
      </DialogHeader>
      {log.error && <Alert variant="destructive">{errorMessage(log.error)}</Alert>}
      {p && isWorking(p) && (
        <output className="grid gap-1.5 rounded-lg border bg-muted/40 px-3 py-2 text-sm">
          <span className="flex items-center gap-2">
            <Loader2 className="size-4 animate-spin" aria-hidden="true" /> {progressWords(p)}
          </span>
          {percent !== null && (
            <progress value={percent} max={100} aria-label={`${repo}: how far`} className="h-1.5 w-full appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary" />
          )}
        </output>
      )}
      {log.isPending ? (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Reading the log…
        </p>
      ) : newest.length === 0 ? (
        <p className="text-sm text-muted-foreground">Nothing yet: the first run writes here.</p>
      ) : (
        <div className="grid max-h-[60vh] gap-4 overflow-y-auto pr-1">
          {newest.map(([run, lines]) => (
            <section key={run} aria-label={`Run of ${when(run / 1000)}`} className="grid gap-1">
              <h3 className="text-xs font-medium text-muted-foreground">{when(run / 1000)}</h3>
              <ol className="grid gap-1 text-sm">
                {lines.map((l, i) => {
                  const { icon: Icon, label, className } = levels[l.level] ?? levels.info
                  return (
                    <li key={i} className="grid grid-cols-[auto_auto_minmax(0,1fr)] items-start gap-2">
                      <time className="pt-px font-mono text-xs text-muted-foreground tabular-nums" dateTime={new Date(l.at * 1000).toISOString()}>
                        {time.format(new Date(l.at * 1000))}
                      </time>
                      <Icon className={cn('mt-0.5 size-3.5', className)} aria-label={label} />
                      <span className={cn('[overflow-wrap:anywhere]', l.level !== 'info' && className)}>{l.text}</span>
                    </li>
                  )
                })}
              </ol>
            </section>
          ))}
        </div>
      )}
    </>
  )
}
