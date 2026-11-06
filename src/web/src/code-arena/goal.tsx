import { CircleCheck, CircleSlash, Pause, Play, Target, X } from 'lucide-react'
import { useId, useState } from 'react'
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
import { cancelGoal, goalActive, goalBounds, goalDefaults, pauseGoal, resumeGoal, setGoal, tokens, type CodeGoal, type GoalCheck } from './tasks-api'

// Goal mode: the agent keeps working, turn after turn, until a goal is met as its check says, within the budget set here.

type CheckChoice = 'agent' | 'command' | 'condition'

const checks: { value: CheckChoice; label: string; hint: string }[] = [
  { value: 'command', label: 'A command succeeds', hint: 'Run after each turn; the goal is met on exit code 0 (the tests, a build).' },
  { value: 'condition', label: 'A condition holds', hint: 'A fresh reader looks at the code after each turn and says whether it holds.' },
  { value: 'agent', label: 'The agent writes it down first', hint: 'It says how it will check before it changes anything (a command asks as commands do).' },
]

/** Set a goal: what it is, how it is checked, and what it may spend. */
export function GoalDialog({ open, onOpenChange, onSet, initial }: { open: boolean; onOpenChange: (open: boolean) => void; onSet: () => void; initial?: string }) {
  const [text, setText] = useState(initial ?? '')
  const [choice, setChoice] = useState<CheckChoice>('command')
  const [command, setCommand] = useState('')
  const [condition, setCondition] = useState('')
  const [steps, setSteps] = useState(String(goalDefaults.steps))
  const [minutes, setMinutes] = useState(String(goalDefaults.minutes))
  const [budgetTokens, setBudgetTokens] = useState(String(goalDefaults.tokens))
  const [busy, setBusy] = useState(false)
  const n = (s: string) => Number(s.replace(/[_,\s]/g, ''))
  const budget = { steps: n(steps), minutes: n(minutes), tokens: n(budgetTokens) }
  const inBounds = Number.isInteger(budget.steps) && budget.steps >= 1 && budget.steps <= goalBounds.steps
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
          <DialogTitle>Work toward a goal</DialogTitle>
          <DialogDescription>The agent keeps working, turn after turn, until the check passes or the budget is spent, then says how it checked.</DialogDescription>
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
            {!inBounds && <p className="text-xs text-destructive-ink">The budget is out of bounds.</p>}
          </fieldset>
        </form>
        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="submit" form="goal-form" disabled={!ready || busy}>
            {busy ? <Spinner /> : <Target />} Start
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
}

/** What the goal spent of its budget, as a few words. */
function goalUsage(g: CodeGoal): string {
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
export function GoalBar({ goal, onChanged, onDismiss }: { goal: CodeGoal; onChanged: () => void; onDismiss: () => void }) {
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
      {active && (
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
    </section>
  )
}
