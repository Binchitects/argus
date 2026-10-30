import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, ChevronsDownUp, CircleAlert, CircleCheck, Ellipsis, ExternalLink, FileCode, Info, Play, ShieldAlert, TriangleAlert, Wand2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { asks, chatBridge, problemText } from './bridge'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { nameOf, parentOf, problemsQuery, readSecurity, runCheck, securityProblemsQuery } from './ide-api'
import { problemFilter, problemLine, useProblems, type Problem } from './problems-store'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const icons = { error: CircleAlert, warning: TriangleAlert, info: Info }
const colors = { error: 'text-destructive-ink', warning: 'text-warning-ink', info: 'text-muted-foreground' }

type Severity = Problem['severity']

/**
 * The project's check (its build, type check or linter) and what it found, by file, then what the language servers
 * running found, then the editor's own services (TypeScript's, JSON's, CSS's, HTML's): each problem opens the file at its
 * line, and Fix asks the agent about it. As VS Code's: a filter (text, a glob of files, `!` to leave out), each severity
 * shown or not, the active file only, files folded, and each problem copied or quick-fixed.
 */
export function ProblemsPanel() {
  const queryClient = useQueryClient()
  const last = useQuery(problemsQuery)
  const run = useMutation({
    mutationFn: runCheck,
    onSuccess: (r) => queryClient.setQueryData(problemsQuery.queryKey, r),
    onError: (e) => toast.error(errorMessage(e)),
  })
  // Arena's security review of this repository: read as the view opens, again on asking; its findings join the problems.
  const security = useQuery(securityProblemsQuery)
  const reread = useMutation({
    mutationFn: readSecurity,
    onSuccess: (r) => {
      queryClient.setQueryData(securityProblemsQuery.queryKey, r)
      void queryClient.invalidateQueries({ queryKey: problemsQuery.queryKey, exact: true })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const securityRead = security.dataUpdatedAt
  useEffect(() => {
    if (securityRead) void queryClient.invalidateQueries({ queryKey: problemsQuery.queryKey, exact: true })
  }, [securityRead, queryClient])
  const all = useProblems()
  const { active } = useEditor()
  const [filter, setFilter] = useState('')
  const [shown, setShown] = useState<Record<Severity, boolean>>({ error: true, warning: true, info: true })
  const [activeOnly, setActiveOnly] = useState(false)
  const [folded, setFolded] = useState<ReadonlySet<string>>(new Set())
  const activePath = active?.kind === 'file' || active?.kind === 'preview' ? active.path : null
  const matches = problemFilter(filter)
  const keep = (p: Problem) => shown[p.severity] && (!activeOnly || p.path === activePath) && matches(p)
  const data = last.data
  const problems = all.filter((p) => p.source === 'check' && keep(p))
  const fromServers = all.filter((p) => p.source === 'server' && keep(p))
  const fromEditor = all.filter((p) => p.source === 'editor' && keep(p))
  const hidden = all.length - problems.length - fromServers.length - fromEditor.length
  const filesOf = (list: Problem[]) => [...new Set(list.map((p) => p.path))]
  const files = filesOf(problems)
  const serverFiles = filesOf(fromServers)
  const editorFiles = filesOf(fromEditor)
  const fold = (key: string) => setFolded((f) => new Set(f.has(key) ? [...f].filter((k) => k !== key) : [...f, key]))
  const running = run.isPending || !!data?.running
  const toggle = (severity: Severity, label: string, Icon: typeof CircleAlert) => (
    <Tooltip content={label}>
      <button type="button" aria-label={label} aria-pressed={shown[severity]} onClick={() => setShown((s) => ({ ...s, [severity]: !s[severity] }))} className={cn(action, shown[severity] && 'bg-accent text-foreground')}>
        <Icon />
      </button>
    </Tooltip>
  )
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Problems</h2>
        <Tooltip content="Collapse All">
          <button
            type="button"
            aria-label="Collapse All"
            onClick={() => setFolded(new Set([...files.map((f) => `check:${f}`), ...serverFiles.map((f) => `server:${f}`), ...editorFiles.map((f) => `editor:${f}`)]))}
            className={action}
          >
            <ChevronsDownUp />
          </button>
        </Tooltip>
        <Tooltip content={data?.command ? `Run check: ${data.command}` : 'Run check'}>
          <button type="button" aria-label="Run check" onClick={() => run.mutate()} disabled={running || !data?.command} className={action}>
            <Play className={cn(running && 'animate-pulse')} />
          </button>
        </Tooltip>
        <Tooltip content="Read Arena's security review again">
          <button type="button" aria-label="Read the security review again" onClick={() => reread.mutate()} disabled={reread.isPending} className={action}>
            <ShieldAlert className={cn(reread.isPending && 'animate-pulse')} />
          </button>
        </Tooltip>
        <PartHelp part="problems" />
      </div>
      <div className="flex shrink-0 items-center gap-1 pr-1.5 pb-1.5 pl-3">
        <input
          type="search"
          aria-label="Filter problems"
          placeholder="Filter (text, **/*.ts, !exclude)"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          className="h-6 min-w-0 flex-1 rounded-sm border border-input bg-background px-1.5 text-xs outline-none focus-visible:ring-1 focus-visible:ring-ring"
        />
        {toggle('error', 'Show errors', CircleAlert)}
        {toggle('warning', 'Show warnings', TriangleAlert)}
        {toggle('info', 'Show infos', Info)}
        <Tooltip content="Show active file only">
          <button type="button" aria-label="Show active file only" aria-pressed={activeOnly} onClick={() => setActiveOnly((on) => !on)} className={cn(action, activeOnly && 'bg-accent text-foreground')}>
            <FileCode />
          </button>
        </Tooltip>
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        {hidden > 0 && <p className="px-3 pb-2 text-xs text-muted-foreground">{`${hidden} ${hidden === 1 ? 'problem' : 'problems'} hidden by the filter`}</p>}
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
                  : data.problems.length === 0
                    ? data.exitCode === 0
                      ? 'No problems.'
                      : `The check failed (exit code ${data.exitCode ?? '—'}): ${data.said ?? 'nothing it printed reads as a problem'}`
                    : `${count(data.problems, 'error')} · ${count(data.problems, 'warning')} · ran ${new Date(data.ran).toLocaleTimeString()}`}
          </p>
        )}
        {security.data && (
          <p className="flex flex-wrap items-center gap-x-1.5 px-3 pb-2 text-xs text-muted-foreground" aria-label="Security review">
            <ShieldAlert className="size-3.5" aria-hidden="true" />
            {security.data.said ?? (
              <>
                Security review of {security.data.project}
                {security.data.ref ? ` at ${security.data.ref}` : ''}: {security.data.count} open {security.data.count === 1 ? 'finding' : 'findings'}
                {security.data.finishedAt ? `, ${new Date(security.data.finishedAt).toLocaleDateString()}` : ''}
                {security.data.link && (
                  <a href={security.data.link} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-0.5 text-primary hover:underline">
                    in Arena <ExternalLink className="size-3" aria-hidden="true" />
                  </a>
                )}
              </>
            )}
          </p>
        )}
        {data?.ran && data.problems.length === 0 && data.exitCode === 0 && !running && (
          <div className="grid justify-items-center gap-2 px-6 py-6 text-center text-muted-foreground">
            <CircleCheck className="size-6 opacity-60" aria-hidden="true" />
          </div>
        )}
        {files.map((file) => (
          <FileProblems key={file} file={file} problems={problems.filter((p) => p.path === file)} folded={folded.has(`check:${file}`)} onFold={() => fold(`check:${file}`)} />
        ))}
        {serverFiles.length > 0 && (
          <>
            <h3 className="px-3 pt-3 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Language servers</h3>
            <p className="px-3 pb-2 text-xs text-muted-foreground">{`${count(fromServers, 'error')} · ${count(fromServers, 'warning')}, as the servers running see the files`}</p>
            {serverFiles.map((file) => (
              <FileProblems
                key={`lsp:${file}`}
                file={file}
                label={`${file}, from the language servers`}
                problems={fromServers.filter((p) => p.path === file)}
                folded={folded.has(`server:${file}`)}
                onFold={() => fold(`server:${file}`)}
              />
            ))}
          </>
        )}
        {editorFiles.length > 0 && (
          <>
            <h3 className="px-3 pt-3 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">The editor</h3>
            <p className="px-3 pb-2 text-xs text-muted-foreground">{`${count(fromEditor, 'error')} · ${count(fromEditor, 'warning')}, from the editor's own services for the files open`}</p>
            {editorFiles.map((file) => (
              <FileProblems
                key={`editor:${file}`}
                file={file}
                label={`${file}, from the editor`}
                problems={fromEditor.filter((p) => p.path === file)}
                folded={folded.has(`editor:${file}`)}
                onFold={() => fold(`editor:${file}`)}
              />
            ))}
          </>
        )}
      </div>
    </div>
  )
}

const count = (problems: { severity: Severity }[], severity: 'error' | 'warning') => {
  const n = problems.filter((p) => p.severity === severity).length
  return `${n} ${severity}${n === 1 ? '' : 's'}`
}

/** One file's problems, folded or not: each opens the file at its line; Fix asks the agent about it; its menu copies or quick-fixes it. */
function FileProblems({ file, problems, label, folded, onFold }: { file: string; problems: Problem[]; label?: string; folded: boolean; onFold: () => void }) {
  const editor = useEditor()
  const Chevron = folded ? ChevronRight : ChevronDown
  const copy = (text: string) =>
    void navigator.clipboard?.writeText(text).then(
      () => toast.success('Copied.'),
      () => toast.error('The browser did not let the page copy it.'),
    )
  const quickFix = async (p: Problem) => {
    await editor.open(p.path, { line: p.line, column: p.column })
    editor.runAction('editor.action.quickFix')
  }
  return (
    <section aria-label={label ?? file}>
      <h3 className="flex h-[1.375rem] items-center font-medium">
        <button
          type="button"
          aria-expanded={!folded}
          onClick={onFold}
          className="flex min-w-0 flex-1 items-center gap-1.5 pl-1 text-left outline-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
        >
          <Chevron className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
          <FileIcon path={file} />
          <span className="truncate">{nameOf(file)}</span>
          <span className="min-w-0 truncate text-xs font-normal text-muted-foreground">{parentOf(file)}</span>
          <span className="ml-auto pr-2 text-xs font-normal text-muted-foreground">{problems.length}</span>
        </button>
      </h3>
      {!folded && <ul aria-label={`Problems in ${file}`}>
        {problems.map((p, i) => {
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
                    {p.server ? `${p.server} ` : ''}{p.code ? `${p.code} ` : ''}[{p.line}, {p.column}]
                  </span>
                </span>
              </button>
              <Tooltip content="Fix with Code Arena">
                <button
                  type="button"
                  aria-label={`Fix ${p.path}:${p.line}`}
                  onClick={() => chatBridge.current?.ask(p.ask ?? asks.fix([problemText(p)]), [{ path: p.path, startLine: p.line, endLine: p.line }])}
                  className={cn(action, 'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100')}
                >
                  <Wand2 />
                </button>
              </Tooltip>
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <button type="button" aria-label={`More actions for ${p.path}:${p.line}`} className={cn(action, 'opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 data-[state=open]:opacity-100')}>
                    <Ellipsis />
                  </button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuItem onSelect={() => copy(problemLine(p))}>Copy</DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => copy(p.message)}>Copy Message</DropdownMenuItem>
                  <DropdownMenuItem onSelect={() => void quickFix(p)}>Quick Fix…</DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </li>
          )
        })}
      </ul>}
    </section>
  )
}
