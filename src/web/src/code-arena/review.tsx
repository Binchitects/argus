import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronRight, CircleAlert, CircleCheck, CircleDot, Info, MessageSquareText, RotateCcw, ScanSearch, Square, TriangleAlert, Wand2, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { chatBridge } from './bridge'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { markFinding, nameOf, parentOf, problemsQuery, reviewQuery, startReview, stopReview, type ReviewFinding, type ReviewRun } from './ide-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const severityIcon = { critical: CircleAlert, major: CircleAlert, minor: TriangleAlert, nit: Info }
const severityColor = { critical: 'text-destructive-ink', major: 'text-destructive-ink', minor: 'text-warning-ink', nit: 'text-muted-foreground' }
const verdictColor = { 'looks good': 'text-success-ink', comments: 'text-warning-ink', 'changes needed': 'text-destructive-ink' }

/** How a finding reads to the agent: where, what, why and the fix. */
const findingText = (f: ReviewFinding) => `${f.path}:${f.line} (${f.severity}) ${f.title}. ${f.why} Fix: ${f.fix}`

/**
 * The Review view: a review of the changes not yet committed, or of the branch over its base, by a reviewer that
 * reads the code and changes nothing; its steps while it runs, then its verdict and findings by file, worst first.
 * A finding opens the file at its line (marked there, as Problems are); Apply fix makes the fix in the editor (undo
 * takes it back, saving is the person's), Ask the agent hands it to the chat, Dismiss leaves it.
 */
export function ReviewPanel() {
  const queryClient = useQueryClient()
  const last = useQuery(reviewQuery)
  const run = last.data?.review ?? null
  const [scope, setScope] = useState<'changes' | 'branch'>(run?.base ? 'branch' : 'changes')
  const [base, setBase] = useState(run?.base ?? 'main')
  const set = (data: { review: ReviewRun | null }) => queryClient.setQueryData(reviewQuery.queryKey, data)
  const start = useMutation({ mutationFn: () => startReview(scope === 'branch' ? base.trim() : null), onSuccess: set, onError: (e) => toast.error(errorMessage(e)) })
  const stop = useMutation({ mutationFn: stopReview, onSuccess: set, onError: (e) => toast.error(errorMessage(e)) })
  // A review over: its findings are Problems now, marked in the files.
  const wasRunning = useRef(false)
  const running = !!run?.running
  useEffect(() => {
    if (wasRunning.current && !running) void queryClient.invalidateQueries({ queryKey: problemsQuery.queryKey })
    wasRunning.current = running
  }, [running, queryClient])
  const files = [...new Set((run?.findings ?? []).map((f) => f.path))]
  const open = (run?.findings ?? []).filter((f) => f.state === 'open').length
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Review</h2>
        <PartHelp part="review" />
      </div>
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]">
        <form
          className="grid gap-2 px-3 pb-3"
          onSubmit={(e) => {
            e.preventDefault()
            start.mutate()
          }}
        >
          <fieldset className="grid gap-1" disabled={running}>
            <legend className="sr-only">What to review</legend>
            <label className="flex items-center gap-2">
              <input type="radio" name="review-scope" className="size-3.5 accent-primary" checked={scope === 'changes'} onChange={() => setScope('changes')} />
              The changes not yet committed
            </label>
            <label className="flex items-center gap-2">
              <input type="radio" name="review-scope" className="size-3.5 accent-primary" checked={scope === 'branch'} onChange={() => setScope('branch')} />
              This branch over
            </label>
            {scope === 'branch' && (
              <input
                aria-label="Base branch"
                value={base}
                onChange={(e) => setBase(e.target.value)}
                spellCheck={false}
                className="ml-5 h-7 rounded-sm border bg-background px-2 font-mono text-xs outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
            )}
          </fieldset>
          {running ? (
            <button type="button" onClick={() => stop.mutate()} className="inline-flex h-7 items-center justify-center gap-1.5 rounded-sm border px-3 text-xs font-medium hover:bg-accent">
              <Square className="size-3 fill-current" /> Stop the review
            </button>
          ) : (
            <button
              type="submit"
              disabled={start.isPending || (scope === 'branch' && !base.trim())}
              className="inline-flex h-7 items-center justify-center gap-1.5 rounded-sm bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:opacity-50"
            >
              <ScanSearch className="size-3.5" /> {run ? 'Review again' : 'Review'}
            </button>
          )}
        </form>
        {last.error && <p className="px-3 py-2 text-muted-foreground">{errorMessage(last.error)}</p>}
        {!run && !last.isPending && !last.error && (
          <p className="px-3 text-xs text-muted-foreground">A reviewer reads the changes and the code around them, tries to refute each finding, and changes nothing.</p>
        )}
        {run && <ReviewResult run={run} files={files} open={open} />}
      </div>
    </div>
  )
}

function ReviewResult({ run, files, open }: { run: ReviewRun; files: string[]; open: number }) {
  if (run.running)
    return (
      <div className="grid gap-1 px-3" aria-live="polite">
        <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <CircleDot className="size-3 animate-pulse" aria-hidden="true" />
          Reviewing {run.base ? `this branch over ${run.base}` : 'the changes not yet committed'}…
        </p>
        <ol aria-label="What the reviewer did" className="grid gap-0.5 font-mono text-[0.6875rem] text-muted-foreground">
          {run.steps.slice(-10).map((s, i) => (
            <li key={`${run.steps.length - 10 + i}`} className="truncate" title={s}>
              {s}
            </li>
          ))}
        </ol>
      </div>
    )
  return (
    <div className="grid gap-2">
      {run.error ? (
        <p className="px-3 text-xs text-destructive-ink">{run.error}</p>
      ) : (
        <div className="grid gap-1 px-3">
          {run.verdict && (
            <p className={cn('flex items-center gap-1.5 text-xs font-semibold', verdictColor[run.verdict])}>
              {run.verdict === 'looks good' ? <CircleCheck className="size-3.5" aria-hidden="true" /> : <CircleAlert className="size-3.5" aria-hidden="true" />}
              {run.verdict.charAt(0).toUpperCase() + run.verdict.slice(1)}
              <span className="font-normal text-muted-foreground">
                · {run.findings.length === 0 ? 'no findings' : `${open} of ${run.findings.length} open`}
              </span>
            </p>
          )}
          {run.summary && <p className="text-xs text-muted-foreground">{run.summary}</p>}
        </div>
      )}
      {files.map((file) => (
        <FileFindings key={file} file={file} findings={run.findings.filter((f) => f.path === file)} />
      ))}
    </div>
  )
}

/** One file's findings: each opens the file at its line; its why and fix unfold. */
function FileFindings({ file, findings }: { file: string; findings: ReviewFinding[] }) {
  return (
    <section aria-label={file}>
      <h3 className="flex h-[1.375rem] items-center gap-1.5 pl-3 font-medium">
        <FileIcon path={file} />
        <span className="truncate">{nameOf(file)}</span>
        <span className="min-w-0 truncate text-xs font-normal text-muted-foreground">{parentOf(file)}</span>
      </h3>
      <ul aria-label={`Findings in ${file}`}>
        {findings.map((f) => (
          <Finding key={f.index} finding={f} />
        ))}
      </ul>
    </section>
  )
}

function Finding({ finding: f }: { finding: ReviewFinding }) {
  const editor = useEditor()
  const queryClient = useQueryClient()
  const [unfolded, setUnfolded] = useState(false)
  const mark = useMutation({
    mutationFn: (state: ReviewFinding['state']) => markFinding(f.index, state),
    onSuccess: (data) => {
      queryClient.setQueryData(reviewQuery.queryKey, data)
      void queryClient.invalidateQueries({ queryKey: problemsQuery.queryKey })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const Icon = severityIcon[f.severity]
  const done = f.state !== 'open'
  // The fix made in the editor, as one edit undo takes back; the file is the person's to save.
  const apply = async () => {
    await editor.open(f.path, { line: f.line, column: 1 })
    const model = editor.activeModel()
    if (!model || f.old === null || f.new === null) return
    const eol = model.getEOL()
    const [old, replacement] = [f.old, f.new].map((t) => t.replace(/\r\n/g, '\n').replace(/\n/g, eol))
    const found = model.findMatches(old!, false, false, true, null, false, 2)
    if (found.length !== 1) {
      toast.error('The fix no longer fits the file (it changed since the review): ask the agent instead.')
      return
    }
    model.pushEditOperations([], [{ range: found[0]!.range, text: replacement! }], () => null)
    mark.mutate('fixed')
  }
  return (
    <li className={cn('group grid pr-1.5 hover:bg-accent/40', done && 'opacity-60')}>
      <div className="flex items-start">
        <button
          type="button"
          aria-expanded={unfolded}
          aria-label={unfolded ? `Fold ${f.title}` : `Unfold ${f.title}`}
          onClick={() => setUnfolded(!unfolded)}
          className="mt-0.5 ml-1.5 grid size-4 shrink-0 place-items-center rounded-sm text-muted-foreground outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          <ChevronRight className={cn('size-3 transition-transform', unfolded && 'rotate-90')} />
        </button>
        <button
          type="button"
          onClick={() => void editor.open(f.path, { line: f.line, column: 1 })}
          className="flex min-w-0 flex-1 items-start gap-1.5 py-0.5 pl-0.5 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
          aria-label={`${f.title}, ${f.severity}, line ${f.line}${done ? `, ${f.state}` : ''}`}
          title={`${f.path}:${f.line} ${f.title}`}
        >
          <Icon className={cn('mt-0.5 size-3.5 shrink-0', severityColor[f.severity])} aria-label={f.severity} />
          <span className={cn('min-w-0 break-words', done && 'line-through')}>
            {f.title}
            <span className="ml-1 text-xs text-muted-foreground">
              {f.severity} [{f.line}
              {f.endLine > f.line ? `–${f.endLine}` : ''}]{done ? ` · ${f.state}` : ''}
            </span>
          </span>
        </button>
        {!done && f.old !== null && (
          <Tooltip content="Apply fix in the editor">
            <button type="button" aria-label={`Apply the fix for ${f.title}`} onClick={() => void apply()} className={action}>
              <Wand2 />
            </button>
          </Tooltip>
        )}
        {!done && (
          <Tooltip content="Ask the agent to fix it">
            <button
              type="button"
              aria-label={`Ask the agent about ${f.title}`}
              onClick={() => chatBridge.current?.ask(`Fix this review finding: ${findingText(f)}`, [{ path: f.path, startLine: f.line, endLine: f.endLine }])}
              className={action}
            >
              <MessageSquareText />
            </button>
          </Tooltip>
        )}
        <Tooltip content={done ? 'Open again' : 'Dismiss'}>
          <button type="button" aria-label={done ? `Open ${f.title} again` : `Dismiss ${f.title}`} onClick={() => mark.mutate(done ? 'open' : 'dismissed')} className={action}>
            {done ? <RotateCcw /> : <X />}
          </button>
        </Tooltip>
      </div>
      {unfolded && (
        <div className="grid gap-1 pt-0.5 pb-1.5 pl-[2.375rem] text-xs">
          {f.why && <p className="text-muted-foreground">{f.why}</p>}
          {f.fix && (
            <p>
              <span className="font-medium">Fix: </span>
              {f.fix}
            </p>
          )}
          {f.new !== null && <pre className="overflow-auto rounded-sm bg-muted/60 px-2 py-1 font-mono text-[0.6875rem] whitespace-pre">{f.new}</pre>}
        </div>
      )}
    </li>
  )
}
