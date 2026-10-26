import { useQuery } from '@tanstack/react-query'
import { CircleCheck, CircleDashed, CircleDot, CircleSlash, ExternalLink, FileCode } from 'lucide-react'
import { cn } from '@/lib/utils'
import { planQuery, type CodePlanStep } from './api'
import { useEditor } from './editor-state'

const marks: Record<CodePlanStep['status'], { icon: typeof CircleCheck; label: string; className: string }> = {
  done: { icon: CircleCheck, label: 'Done', className: 'text-success' },
  doing: { icon: CircleDot, label: 'Doing', className: 'text-primary' },
  blocked: { icon: CircleSlash, label: 'Blocked', className: 'text-destructive' },
  todo: { icon: CircleDashed, label: 'To do', className: 'text-muted-foreground' },
}

/**
 * The side bar's Plan: the agent's working plan as it changes (its plan tool), each step with what done means, how its
 * check went and the files it changes (a click opens one); where Arena shows it, when the session is kept in step.
 */
export function PlanPanel({ arena }: { arena: string | null }) {
  const plan = useQuery(planQuery)
  const editor = useEditor()
  const p = plan.data
  const done = p?.steps.filter((s) => s.status === 'done').length ?? 0
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Plan</h2>
        {p && p.steps.length > 0 && (
          <span className="text-xs text-muted-foreground tabular-nums">
            {done}/{p.steps.length} done
          </span>
        )}
      </div>
      {!p || (p.steps.length === 0 && !p.title) ? (
        <p className="px-3 text-xs text-muted-foreground">No plan yet: for work of several steps the agent makes one before it starts, and keeps it here as it goes.</p>
      ) : (
        <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-3">
          <p className="text-sm font-medium">{p.title}</p>
          {p.goal && <p className="mt-0.5 text-xs text-muted-foreground">{p.goal}</p>}
          {arena && p.chat && (
            <a href={`${arena.replace(/\/$/, '')}/chat/${p.chat}`} target="_blank" rel="noopener noreferrer" className="mt-1 inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
              In Arena, with its chat <ExternalLink className="size-3" aria-hidden="true" />
            </a>
          )}
          <ol className="mt-2 grid gap-2" aria-label="Steps">
            {p.steps.map((s) => {
              const mark = marks[s.status]
              const Icon = mark.icon
              return (
                <li key={s.number} className={cn('grid gap-0.5 rounded border p-2 text-xs', s.status === 'doing' && 'border-primary/50 bg-primary/5')}>
                  <p className="flex items-start gap-1.5">
                    <Icon className={cn('mt-px size-3.5 shrink-0', mark.className)} aria-label={mark.label} />
                    <span className={cn('min-w-0 flex-1', s.status === 'done' && 'text-muted-foreground')}>
                      {s.number}. {s.title}
                    </span>
                  </p>
                  {s.check && (
                    <p className="ms-5 text-muted-foreground">
                      Done when: <code className="break-words text-foreground">{s.check}</code>
                      {s.checkResult && (
                        <span title={s.checkOutput ?? undefined} className={cn('ms-1 font-medium', s.checkResult === 'passed' ? 'text-success' : 'text-destructive')}>
                          {s.checkResult === 'passed' ? '· passed' : '· failed'}
                        </span>
                      )}
                    </p>
                  )}
                  {s.dependsOn.length > 0 && <p className="ms-5 text-muted-foreground">after {s.dependsOn.join(', ')}</p>}
                  {s.files.length > 0 && (
                    <ul className="ms-5 flex flex-wrap gap-1" aria-label={`Step ${s.number}'s files`}>
                      {s.files.map((f) => (
                        <li key={f}>
                          <button type="button" onClick={() => void editor.open(f)} className="flex items-center gap-1 rounded bg-muted px-1 py-0.5 hover:bg-accent">
                            <FileCode className="size-3" aria-hidden="true" />
                            {f}
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  {s.notes && <p className="ms-5 whitespace-pre-wrap text-muted-foreground">{s.notes}</p>}
                </li>
              )
            })}
          </ol>
        </div>
      )}
    </div>
  )
}
