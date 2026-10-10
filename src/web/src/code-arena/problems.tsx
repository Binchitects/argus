import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CircleAlert, CircleCheck, Info, Play, TriangleAlert, Wand2 } from 'lucide-react'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { asks, chatBridge, problemText } from './bridge'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { nameOf, parentOf, problemsQuery, runCheck, type CheckProblem } from './ide-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const icons = { error: CircleAlert, warning: TriangleAlert, info: Info }
const colors = { error: 'text-destructive-ink', warning: 'text-warning-ink', info: 'text-muted-foreground' }

/**
 * The project's check (its build, type check or linter) and what it found, by file: each problem opens the file at its
 * line, and Fix asks the agent about it. The editor marks them in the files too.
 */
export function ProblemsPanel() {
  const editor = useEditor()
  const queryClient = useQueryClient()
  const last = useQuery(problemsQuery)
  const run = useMutation({
    mutationFn: runCheck,
    onSuccess: (r) => queryClient.setQueryData(problemsQuery.queryKey, r),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const data = last.data
  const problems = data?.problems ?? []
  const files = [...new Set(problems.map((p) => p.path))]
  const running = run.isPending || !!data?.running
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Problems</h2>
        <Tooltip content={data?.command ? `Run check: ${data.command}` : 'Run check'}>
          <button type="button" aria-label="Run check" onClick={() => run.mutate()} disabled={running || !data?.command} className={action}>
            <Play className={cn(running && 'animate-pulse')} />
          </button>
        </Tooltip>
        <PartHelp part="problems" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        {last.isPending && Array.from({ length: 3 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
        {last.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(last.error)}</p>}
        {data && (
          <p className="px-3 pb-2 text-xs text-muted-foreground">
            {running
              ? `Running ${data.command}…`
              : !data.command
                ? 'No check found for this folder: set "checkCommand" in config.json (a build, a type check or a linter).'
                : !data.ran
                  ? <>Run the check (<code className="font-mono">{data.command}</code>) to see its problems.</>
                  : problems.length === 0
                    ? data.exitCode === 0
                      ? 'No problems.'
                      : `The check failed (exit code ${data.exitCode ?? '—'}): ${data.said ?? 'nothing it printed reads as a problem'}`
                    : `${count(problems, 'error')} · ${count(problems, 'warning')} · ran ${new Date(data.ran).toLocaleTimeString()}`}
          </p>
        )}
        {data?.ran && problems.length === 0 && data.exitCode === 0 && !running && (
          <div className="grid justify-items-center gap-2 px-6 py-6 text-center text-muted-foreground">
            <CircleCheck className="size-6 opacity-60" aria-hidden="true" />
          </div>
        )}
        {files.map((file) => (
          <section key={file} aria-label={file}>
            <h3 className="flex h-[1.375rem] items-center gap-1.5 pl-3 font-medium">
              <FileIcon path={file} />
              <span className="truncate">{nameOf(file)}</span>
              <span className="min-w-0 truncate text-xs font-normal text-muted-foreground">{parentOf(file)}</span>
            </h3>
            <ul aria-label={`Problems in ${file}`}>
              {problems
                .filter((p) => p.path === file)
                .map((p, i) => {
                  const Icon = icons[p.severity]
                  return (
                    <li key={i} className="group flex items-start pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
                      <button
                        type="button"
                        onClick={() => void editor.open(p.path, { line: p.line, column: p.column })}
                        className="flex min-w-0 flex-1 items-start gap-1.5 py-0.5 pl-6 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
                        title={`${p.path}:${p.line}:${p.column} ${p.message}`}
                      >
                        <Icon className={cn('mt-0.5 size-3.5 shrink-0', colors[p.severity])} aria-label={p.severity} />
                        <span className="min-w-0 break-words">
                          {p.message}
                          <span className="ml-1 text-xs text-muted-foreground">
                            {p.code ? `${p.code} ` : ''}[{p.line}, {p.column}]
                          </span>
                        </span>
                      </button>
                      <Tooltip content="Fix with Code Arena">
                        <button
                          type="button"
                          aria-label={`Fix ${p.path}:${p.line}`}
                          onClick={() => chatBridge.current?.ask(asks.fix([problemText(p)]), [{ path: p.path, startLine: p.line, endLine: p.line }])}
                          className={cn(action, 'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100')}
                        >
                          <Wand2 />
                        </button>
                      </Tooltip>
                    </li>
                  )
                })}
            </ul>
          </section>
        ))}
      </div>
    </div>
  )
}

const count = (problems: CheckProblem[], severity: 'error' | 'warning') => {
  const n = problems.filter((p) => p.severity === severity).length
  return `${n} ${severity}${n === 1 ? '' : 's'}`
}
