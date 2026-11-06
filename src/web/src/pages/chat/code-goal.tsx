import { useQuery } from '@tanstack/react-query'
import { CircleCheck, CircleSlash, Target } from 'lucide-react'
import { useId } from 'react'
import { cn } from '@/lib/utils'
import { codeGoalQuery, type ChatCodeGoal } from './code-goal-api'
import { seconds } from './format'

const words: Record<ChatCodeGoal['status'], string> = {
  working: 'being worked on in Code Arena',
  paused: 'paused in Code Arena',
  met: 'met',
  budget: 'stopped: its budget is spent',
  stuck: 'stopped: no progress',
  cancelled: 'cancelled',
}

const short = (n: number) => (n < 1000 ? String(n) : n < 1_000_000 ? `${Math.round(n / 1000)}k` : `${(n / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`)

/**
 * A Code Arena session's goal above its chat: what the agent works toward there, where it stands, what it spent of its
 * budget and how it is checked; once ended, how. Code Arena alone changes it.
 */
export function CodeGoalLine({ chat }: { chat: string }) {
  const q = useQuery(codeGoalQuery(chat))
  const heading = useId()
  const g = q.data
  if (!g) return null
  const active = g.status === 'working' || g.status === 'paused'
  return (
    <section aria-labelledby={heading} className={cn('flex min-w-0 items-start gap-2 rounded-lg border bg-card px-3 py-2 text-sm', g.status === 'working' && 'border-primary/40')}>
      {g.status === 'met' ? (
        <CircleCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
      ) : active ? (
        <Target className="mt-0.5 size-4 shrink-0 text-primary-ink" aria-hidden="true" />
      ) : (
        <CircleSlash className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      )}
      <div className="grid min-w-0 gap-0.5">
        <p className="min-w-0">
          <span id={heading} className="font-medium">
            Goal, {words[g.status]}:
          </span>{' '}
          <bdi className="break-words">{g.text}</bdi>
        </p>
        <p className="text-xs text-muted-foreground tabular-nums">
          {[
            `${g.turns} ${g.turns === 1 ? 'turn' : 'turns'}`,
            `${g.steps} of ${g.budget.steps} steps`,
            `${seconds(g.ms) || '0 s'} of ${g.budget.minutes} min`,
            `${short(g.tokens)} of ${short(g.budget.tokens)} tokens`,
          ].join(' · ')}
          {g.check && (
            <>
              {' · check: '}
              {g.check.kind === 'command' ? <code className="font-mono">{g.check.text}</code> : g.check.text}
            </>
          )}
        </p>
        {g.outcome && <p className="text-xs">{g.outcome}</p>}
      </div>
    </section>
  )
}
