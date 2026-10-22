import { CheckCircle2, PauseCircle, Plus, XCircle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Spinner } from '@/components/ui/spinner'
import { cn } from '@/lib/utils'
import type { Lane, Loc } from './flow-edit'
import { groups, stepIcons, summary, triggerIcons, when } from './flow-words'
import { stepLabels, triggerLabels, type Flow, type FlowStep, type RunStep, type StepKind } from './studio-api'

function Status({ step }: { step: RunStep | undefined }) {
  if (!step) return null
  if (step.status === 'running') return <Spinner label="Running" />
  if (step.status === 'waiting') return <PauseCircle className="size-4 text-warning-ink" aria-label="Waiting" />
  if (step.status === 'failed') return <XCircle className="size-4 text-destructive-ink" aria-label="Failed" />
  return <CheckCircle2 className="size-4 text-success-ink" aria-label="Done" />
}

/** The line between two steps, with its + to put a step there. */
function Join({ lane, index, onInsert }: { lane: Lane; index: number; onInsert?: (lane: Lane, index: number, kind: StepKind) => void }) {
  return (
    <div className="flex flex-col items-center">
      <div className="h-3 w-px bg-border" />
      {onInsert ? (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="outline" size="icon-sm" className="size-6 rounded-full" aria-label="Add a step here">
              <Plus className="size-3.5" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="center">
            {groups.map((g, i) => (
              <div key={g.label}>
                {i > 0 && <DropdownMenuSeparator />}
                <DropdownMenuLabel>{g.label}</DropdownMenuLabel>
                {g.kinds.map((k) => {
                  const Icon = stepIcons[k]
                  return (
                    <DropdownMenuItem key={k} onSelect={() => onInsert(lane, index, k)}>
                      <Icon /> {stepLabels[k]}
                    </DropdownMenuItem>
                  )
                })}
              </div>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      ) : (
        <div className="size-1.5 rounded-full bg-border" />
      )}
      <div className="h-3 w-px bg-border" />
    </div>
  )
}

interface CanvasProps {
  flow: Flow
  selected: Loc | 'trigger' | null
  onSelect?: (at: Loc | 'trigger') => void
  onInsert?: (lane: Lane, index: number, kind: StepKind) => void
  /** How each step last went in the run shown. */
  ran?: Map<string, RunStep>
  /** The step the replay is at. */
  current?: string | null
}

function Node({ step, loc, props }: { step: FlowStep; loc: Loc; props: CanvasProps }) {
  const Icon = stepIcons[step.kind]
  const chosen = props.selected !== 'trigger' && props.selected?.join('.') === loc.join('.')
  const ran = props.ran?.get(step.id)
  return (
    <button
      type="button"
      onClick={() => props.onSelect?.(loc)}
      aria-pressed={chosen}
      aria-label={`${stepLabels[step.kind]}: ${step.name || step.id}`}
      className={cn(
        'flex w-72 max-w-full items-start gap-3 rounded-xl border bg-card p-3 text-left shadow-xs transition outline-none hover:border-primary/50 focus-visible:ring-[3px] focus-visible:ring-ring',
        chosen && 'border-primary ring-2 ring-primary/30',
        props.current === step.id && 'border-primary ring-4 ring-primary/40',
        ran?.status === 'failed' && 'border-destructive/60',
      )}
    >
      <span className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
        <Icon className="size-4" aria-hidden="true" />
      </span>
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5 text-sm font-medium">
          <span className="truncate">{step.name || stepLabels[step.kind]}</span>
          <span className="shrink-0 font-mono text-[0.65rem] text-muted-foreground">{step.id}</span>
        </span>
        <span className="mt-0.5 line-clamp-2 block text-xs break-words text-muted-foreground">{summary(step)}</span>
      </span>
      <Status step={ran} />
    </button>
  )
}

function LaneView({ steps, lane, props }: { steps: FlowStep[]; lane: Lane; props: CanvasProps }) {
  const base: Loc = lane.parent && lane.branch ? [...lane.parent, lane.branch] : []
  return (
    <div className="flex flex-col items-center">
      <Join lane={lane} index={0} onInsert={props.onInsert} />
      {steps.map((s, i) => {
        const loc = [...base, i]
        return (
          <div key={s.id} className="flex flex-col items-center">
            <Node step={s} loc={loc} props={props} />
            {s.kind === 'condition' && (
              <div className="mt-3 grid grid-cols-2 gap-4 border-t border-dashed pt-1">
                {(['then', 'else'] as const).map((b) => (
                  <div key={b} className="flex min-w-40 flex-col items-center">
                    <span className="mt-1 rounded-full bg-muted px-2 py-0.5 text-[0.7rem] font-medium text-muted-foreground">{b === 'then' ? 'Yes' : 'No'}</span>
                    <LaneView steps={s[b] ?? []} lane={{ parent: loc, branch: b }} props={props} />
                  </div>
                ))}
              </div>
            )}
            {s.kind === 'loop' && (
              <div className="mt-2 rounded-xl border border-dashed px-4 pb-2">
                <span className="block pt-1 text-center text-[0.7rem] font-medium text-muted-foreground">For each item</span>
                <LaneView steps={s.steps ?? []} lane={{ parent: loc, branch: 'steps' }} props={props} />
              </div>
            )}
            <Join lane={lane} index={i + 1} onInsert={props.onInsert} />
          </div>
        )
      })}
    </div>
  )
}

/** The workflow drawn: its trigger on top, then its steps joined in order; a condition's two ways side by side, a loop's steps framed. */
export function FlowCanvas(props: CanvasProps) {
  const TriggerIcon = triggerIcons[props.flow.trigger.kind]
  return (
    <section className="flex min-w-fit flex-col items-center py-4" aria-label="The workflow">
      <button
        type="button"
        onClick={() => props.onSelect?.('trigger')}
        aria-pressed={props.selected === 'trigger'}
        aria-label={`Trigger: ${triggerLabels[props.flow.trigger.kind]}`}
        className={cn(
          'flex w-72 max-w-full items-start gap-3 rounded-xl border-2 border-dashed bg-card p-3 text-left outline-none hover:border-primary/50 focus-visible:ring-[3px] focus-visible:ring-ring',
          props.selected === 'trigger' && 'border-primary',
        )}
      >
        <span className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-amber-500/15 text-amber-700 dark:text-amber-400">
          <TriggerIcon className="size-4" aria-hidden="true" />
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-sm font-medium">{triggerLabels[props.flow.trigger.kind]}</span>
          <span className="mt-0.5 block truncate text-xs text-muted-foreground">{when(props.flow.trigger)}</span>
        </span>
      </button>
      <LaneView steps={props.flow.steps} lane={{ parent: null, branch: null }} props={props} />
    </section>
  )
}
