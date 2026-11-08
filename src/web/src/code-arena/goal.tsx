import { CircleCheck, CircleSlash, Pause, Play, Repeat, SkipForward, Target, Trash2, X } from 'lucide-react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useState } from 'react'
import { Checkbox } from '@/components/ui/checkbox'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { seconds } from '@/pages/chat/format'
import { cancelGoal, dropGoal, goalActive, goalBounds, goalDefaults, loopsQuery, nextGoal, pauseGoal, resumeGoal, setGoal, startLoop, stopLoop, tokens, type CodeGoal, type GoalCheck } from './tasks-api'

// Goal mode: the agent keeps working, turn after turn, until a goal is met as its check says, within the budget set here.

type CheckChoice = 'agent' | 'command' | 'condition'

const checks: { value: CheckChoice; label: string; hint: string }[] = [
  { value: 'command', label: 'A command succeeds', hint: 'Run after each turn; the goal is met on exit code 0 (the tests, a build).' },
  { value: 'condition', label: 'A condition holds', hint: 'A fresh reader looks at the code after each turn and says whether it holds.' },
  { value: 'agent', label: 'The agent writes it down first', hint: 'It says how it will check before it changes anything (a command asks as commands do).' },
]

/** Set a goal: what it is, how it is checked, and what it may spend. */
/** queued: a goal is being worked on already, so this one waits its turn behind it. */
export function GoalDialog({ open, onOpenChange, onSet, initial, queued }: { open: boolean; onOpenChange: (open: boolean) => void; onSet: () => void; initial?: string; queued?: boolean }) {
  const [text, setText] = useState(initial ?? '')
  const [choice, setChoice] = useState<CheckChoice>('command')
  const [command, setCommand] = useState('')
  const [condition, setCondition] = useState('')
  const [steps, setSteps] = useState(String(goalDefaults.steps))
  const [minutes, setMinutes] = useState(String(goalDefaults.minutes))
  const [budgetTokens, setBudgetTokens] = useState(String(goalDefaults.tokens))
  const [untilMet, setUntilMet] = useState(false)
  const [busy, setBusy] = useState(false)
  const n = (s: string) => Number(s.replace(/[_,\s]/g, ''))
  const budget = { steps: n(steps), minutes: n(minutes), tokens: n(budgetTokens), untilMet }
  const inBounds = untilMet || Number.isInteger(budget.steps) && budget.steps >= 1 && budget.steps <= goalBounds.steps
    && Number.isInteger(budget.minutes) && budget.minutes >= 1 && budget.minutes <= goalBounds.minutes
    && Number.isInteger(budget.tokens) && budget.tokens >= 10_000 && budget.tokens <= goalBounds.tokens
  const check: GoalCheck | null = choice === 'command' ? { kind: 'command', text: command.trim() } : choice === 'condition' ? { kind: 'condition', text: condition.trim() } : null
  const ready = text.trim().length > 0 && inBounds && (check === null || check.text.length > 0)
  const start = async () => {
    setBusy(true)
    try {
      await setGoal({ text: text.trim(), check, budget })
      onSet()
      onOpenChange(false)
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{queued ? 'Add a goal after this one' : 'Work toward a goal'}</DialogTitle>
          <DialogDescription>
            {queued
              ? 'It waits its turn: it starts once the goals before it are met, and the session carries on until every one is met.'
              : 'The agent keeps working, turn after turn, until the check passes or the budget is spent, then says how it checked.'}
          </DialogDescription>
        </DialogHeader>
        <form
          id="goal-form"
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            if (ready) void start()
          }}
        >
          <Field label="The goal">
            <Textarea value={text} onChange={(e) => setText(e.target.value)} rows={3} maxLength={4000} placeholder="The parser accepts trailing commas, with tests for them." autoFocus />
          </Field>
          <fieldset className="grid gap-2">
            <legend className="mb-1 text-sm font-medium">How it is checked</legend>
            {checks.map((c) => (
              <label key={c.value} className="flex items-start gap-2 text-sm">
                <input type="radio" name="goal-check" className="mt-1 size-3.5 accent-primary" value={c.value} checked={choice === c.value} onChange={() => setChoice(c.value)} />
                <span className="grid gap-0.5">
                  {c.label}
                  <span className="text-xs text-muted-foreground">{c.hint}</span>
                </span>
              </label>
            ))}
            {choice === 'command' && (
              <Input value={command} onChange={(e) => setCommand(e.target.value)} maxLength={2000} placeholder="dotnet test" aria-label="The command" className="font-mono text-sm" spellCheck={false} />
            )}
            {choice === 'condition' && (
              <Textarea value={condition} onChange={(e) => setCondition(e.target.value)} rows={2} maxLength={2000} placeholder="Every public method of Parser has a test." aria-label="The condition" />
            )}
          </fieldset>
          <fieldset className="grid gap-2">
            <legend className="mb-1 text-sm font-medium">Budget</legend>
            <div className="flex items-start gap-2 text-sm">
              <Checkbox id="goal-until-met" checked={untilMet} onCheckedChange={(v) => setUntilMet(v === true)} className="mt-0.5" />
              <label htmlFor="goal-until-met" className="grid gap-0.5">
                Until it is met
                <span className="text-xs text-muted-foreground">No limit of steps, time or tokens: a long session that stops only when the check passes, the agent makes no progress, or you stop it.</span>
              </label>
            </div>
            {!untilMet && (
            <div className="grid gap-2 sm:grid-cols-3">
              <Field label="Steps" hint={`1 to ${goalBounds.steps.toLocaleString()}`}>
                <Input inputMode="numeric" value={steps} onChange={(e) => setSteps(e.target.value)} />
              </Field>
              <Field label="Minutes" hint={`1 to ${goalBounds.minutes.toLocaleString()}`}>
                <Input inputMode="numeric" value={minutes} onChange={(e) => setMinutes(e.target.value)} />
              </Field>
              <Field label="Tokens" hint="10,000 or more">
                <Input inputMode="numeric" value={budgetTokens} onChange={(e) => setBudgetTokens(e.target.value)} />
              </Field>
            </div>
            )}
            {!inBounds && <p className="text-xs text-destructive-ink">The budget is out of bounds.</p>}
          </fieldset>
        </form>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="submit" form="goal-form" disabled={!ready || busy}>
            {busy ? <Spinner /> : <Target />} {queued ? 'Add' : 'Start'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

const statusWords: Record<CodeGoal['status'], string> = {
  working: 'Working',
  paused: 'Paused',
  met: 'Met',
  budget: 'Budget spent',
  stuck: 'Stopped: no progress',
  cancelled: 'Cancelled',
  queued: 'Waiting',
}

/** What the goal spent of its budget, as a few words. */
function goalUsage(g: CodeGoal): string {
  if (g.budget.untilMet) return [`turn ${Math.max(1, g.turns)}`, `${g.steps.toLocaleString()} steps`, seconds(g.ms) || '0 s', `${tokens(g.tokens)} tokens`, 'until it is met'].join(' · ')
  return [
    `turn ${Math.max(1, g.turns)}`,
    `${g.steps.toLocaleString()} of ${g.budget.steps.toLocaleString()} steps`,
    `${seconds(g.ms) || '0 s'} of ${g.budget.minutes} min`,
    `${tokens(g.tokens)} of ${tokens(g.budget.tokens)} tokens`,
  ].join(' · ')
}

/**
 * The goal above the chat's box while it is set: what it is, where it stands, what it spent and how it is checked, with
 * Pause or Resume and Cancel; once ended, how it ended, until put away.
 */
export function GoalBar({ goal, next = [], onChanged, onDismiss }: { goal: CodeGoal; next?: CodeGoal[]; onChanged: () => void; onDismiss: () => void }) {
  const [busy, setBusy] = useState(false)
  const act = async (what: () => Promise<unknown>) => {
    setBusy(true)
    try {
      await what()
      onChanged()
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  const active = goalActive(goal)
  const heading = useId()
  const spent = (part: number, whole: number) => Math.min(100, whole > 0 ? (part / whole) * 100 : 0)
  const most = Math.max(spent(goal.steps, goal.budget.steps), spent(goal.ms, goal.budget.minutes * 60_000), spent(goal.tokens, goal.budget.tokens))
  return (
    <section
      aria-labelledby={heading}
      className={cn('mb-2 grid gap-1.5 rounded-lg border bg-card px-3 py-2 text-sm', goal.status === 'working' && 'border-primary/50', goal.status === 'met' && 'border-success/40')}
    >
      <div className="flex min-w-0 items-start gap-2">
        {goal.status === 'met' ? (
          <CircleCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
        ) : active ? (
          <Target className={cn('mt-0.5 size-4 shrink-0 text-primary-ink', goal.status === 'working' && 'animate-pulse')} aria-hidden="true" />
        ) : (
          <CircleSlash className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        )}
        <p className="min-w-0 flex-1">
          <span id={heading} className="font-medium">
            Goal · {statusWords[goal.status]}:
          </span>{' '}
          <bdi className="break-words">{goal.text}</bdi>
        </p>
        {goal.status === 'working' && (
          <Tooltip content="Pause after this turn">
            <Button variant="ghost" size="icon-sm" className="size-7" aria-label="Pause the goal" disabled={busy} onClick={() => void act(pauseGoal)}>
              <Pause />
            </Button>
          </Tooltip>
        )}
        {goal.status === 'paused' && (
          <Tooltip content="Carry on toward it">
            <Button variant="ghost" size="icon-sm" className="size-7" aria-label="Resume the goal" disabled={busy} onClick={() => void act(resumeGoal)}>
              <Play />
            </Button>
          </Tooltip>
        )}
        {active ? (
          <Tooltip content="Give it up (the turn working on it stops)">
            <Button variant="ghost" size="icon-sm" className="size-7" aria-label="Cancel the goal" disabled={busy} onClick={() => void act(cancelGoal)}>
              <X />
            </Button>
          </Tooltip>
        ) : (
          <Tooltip content="Put it away">
            <Button variant="ghost" size="icon-sm" className="size-7" aria-label="Put the goal away" onClick={onDismiss}>
              <X />
            </Button>
          </Tooltip>
        )}
      </div>
      <p className="text-xs text-muted-foreground tabular-nums">
        {goalUsage(goal)}
        {active && <span className="sr-only">: {Math.round(most)}% of the budget used</span>}
      </p>
      {active && !goal.budget.untilMet && (
        <div className="h-1 overflow-hidden rounded-full bg-muted" aria-hidden="true">
          <div className={cn('h-full rounded-full bg-primary transition-[width]', most >= 90 && 'bg-warning')} style={{ width: `${most}%` }} />
        </div>
      )}
      <p className="text-xs text-muted-foreground">
        {goal.check ? (
          <>
            Check: {goal.check.kind === 'command' ? <code className="font-mono">{goal.check.text}</code> : goal.check.text}
            {goal.check.by === 'agent' && ' (written down by the agent)'}
            {goal.passed === false && goal.checkedAt && ' · not passing yet'}
          </>
        ) : (
          'The agent writes down how it will check it first.'
        )}
      </p>
      {goal.outcome && <p className="text-xs">{goal.outcome}</p>}
      {next.length > 0 && (
        <div className="grid gap-1 border-t pt-1.5">
          <div className="flex items-center gap-2 text-xs font-medium">
            <span className="flex-1">
              Next, each when the one before is met ({next.length})
            </span>
            {!active && (
              <Button variant="outline" size="sm" className="h-6 px-2 text-xs" disabled={busy} onClick={() => void act(nextGoal)}>
                <SkipForward className="size-3.5" /> Start the next
              </Button>
            )}
          </div>
          <ol className="grid gap-0.5 text-xs text-muted-foreground" aria-label="Goals waiting">
            {next.map((g, i) => (
              <li key={`${i}-${g.text}`} className="flex items-center gap-1.5">
                <span className="tabular-nums">{i + 1}.</span>
                <bdi className="min-w-0 flex-1 truncate">{g.text}</bdi>
                <Button variant="ghost" size="icon-sm" className="size-6" aria-label={`Drop goal ${i + 1}`} disabled={busy} onClick={() => void act(() => dropGoal(i + 1))}>
                  <Trash2 className="size-3.5" />
                </Button>
              </li>
            ))}
          </ol>
        </div>
      )}
    </section>
  )
}

const intervals = [
  { value: '15m', label: 'Every 15 minutes' },
  { value: '30m', label: 'Every 30 minutes' },
  { value: '1h', label: 'Every hour' },
  { value: '4h', label: 'Every 4 hours' },
  { value: '1d', label: 'Every day' },
]

/** "1h", "30m" from an interval in milliseconds, as /loop says it. */
const every = (ms: number) => (ms % 86_400_000 === 0 ? `${ms / 86_400_000}d` : ms % 3_600_000 === 0 ? `${ms / 3_600_000}h` : ms % 60_000 === 0 ? `${ms / 60_000}m` : `${Math.round(ms / 1000)}s`)

/**
 * Loops: a prompt run as a background agent every so often while the session is open (/loop), each run a task under Tasks.
 * Listed with their runs, stopped, and added here.
 */
export function LoopsDialog({ open, onOpenChange, initial }: { open: boolean; onOpenChange: (open: boolean) => void; initial?: string }) {
  const queryClient = useQueryClient()
  const loops = useQuery({ ...loopsQuery, enabled: open })
  const [prompt, setPrompt] = useState(initial ?? '')
  const [interval, setInterval] = useState('1h')
  const [edits, setEdits] = useState(false)
  const changed = () => void queryClient.invalidateQueries({ queryKey: loopsQuery.queryKey })
  const add = useMutation({
    mutationFn: () => startLoop({ every: interval, prompt: prompt.trim(), edits }),
    onSuccess: () => {
      setPrompt('')
      changed()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const stop = useMutation({ mutationFn: stopLoop, onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Loops</DialogTitle>
          <DialogDescription>A prompt run as a background agent every so often while this session is open: each run is a task under Tasks, its report reaching the agent with your next message.</DialogDescription>
        </DialogHeader>
        {(loops.data?.loops.length ?? 0) > 0 && (
          <ul className="grid gap-1.5" aria-label="Loops">
            {loops.data!.loops.map((l) => (
              <li key={l.id} className="flex items-start gap-2 rounded-md border px-2.5 py-2 text-sm">
                <Repeat className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <span className="grid min-w-0 flex-1 gap-0.5">
                  <bdi className="break-words">{l.prompt}</bdi>
                  <span className="text-xs text-muted-foreground">
                    {l.id} · every {every(l.everyMs)}
                    {l.edits ? ' · may change files' : ''} · {l.runs} {l.runs === 1 ? 'run' : 'runs'}
                    {l.skipped > 0 ? `, ${l.skipped} skipped` : ''}
                    {l.lastTask ? ` · last ${l.lastTask} ${l.lastStatus}` : ''}
                  </span>
                </span>
                <Button variant="ghost" size="icon-sm" className="size-7" aria-label={`Stop loop ${l.id}`} disabled={stop.isPending} onClick={() => stop.mutate(l.id)}>
                  <X />
                </Button>
              </li>
            ))}
          </ul>
        )}
        <form
          id="loop-form"
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            if (prompt.trim()) add.mutate()
          }}
        >
          <Field label="Each run">
            <Textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} rows={3} maxLength={4000} placeholder="Run the tests; if one fails, find why and say so." />
          </Field>
          <div className="flex flex-wrap items-center gap-3">
            <select value={interval} onChange={(e) => setInterval(e.target.value)} aria-label="How often" className="h-9 rounded-md border bg-background px-2 text-sm">
              {intervals.map((i) => (
                <option key={i.value} value={i.value}>
                  {i.label}
                </option>
              ))}
            </select>
            <div className="flex items-center gap-2 text-sm">
              <Checkbox id="loop-edits" checked={edits} onCheckedChange={(v) => setEdits(v === true)} />
              <label htmlFor="loop-edits">It may change files</label>
            </div>
          </div>
        </form>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Close
          </Button>
          <Button type="submit" form="loop-form" disabled={!prompt.trim() || add.isPending}>
            {add.isPending ? <Spinner /> : <Repeat />} Add the loop
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
