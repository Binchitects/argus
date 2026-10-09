import { KeyValues } from '@/components/app/key-values'
import { Alert } from '@/components/ui/alert'
import { ago, count } from '@/lib/format'
import type { KeyLimits, RateLimit } from '@/lib/rate-limits'
import { cn } from '@/lib/utils'

const refusedFor: Record<string, string> = { requests: 'requests a minute', tokens: 'tokens a minute', 'at once': 'requests at once', other: 'another limit' }

/** Where a limit comes from, in words: for the person themselves, or for an admin looking at them. */
function source(l: RateLimit, you: boolean): string | null {
  switch (l.from) {
    case 'person':
      return you ? 'set for you' : 'their own'
    case 'group':
      return `from ${l.group}`
    case 'company':
      return "the company's"
    default:
      return null
  }
}

function Limit({ limit, used, you }: { limit: RateLimit; used: number | undefined; you: boolean }) {
  const share = limit.value && used !== undefined ? Math.min(1, used / limit.value) : null
  const from = source(limit, you)
  return (
    <div className="grid gap-1">
      <span className="tabular-nums">
        {used !== undefined && (
          <>
            <span className="font-medium">{count(used)}</span>
            <span className="text-muted-foreground"> {limit.value === null ? 'used, ' : 'of '}</span>
          </>
        )}
        {limit.value === null ? 'no limit' : count(limit.value)}
        {from && <span className="text-muted-foreground"> · {from}</span>}
      </span>
      {share !== null && (
        <span className="h-1.5 max-w-64 overflow-hidden rounded-full bg-muted" aria-hidden="true">
          <span className={cn('block h-full rounded-full', share >= 1 ? 'bg-destructive' : share >= 0.8 ? 'bg-warning' : 'bg-primary')} style={{ width: `${share * 100}%` }} />
        </span>
      )}
    </div>
  )
}

/** Each limit with what was used in the last minute and where it comes from, and what was refused in the last day. */
export function LimitRows({ limits, you }: { limits: KeyLimits; you: boolean }) {
  const refused = limits.refused ?? []
  const total = refused.reduce((n, r) => n + r.count, 0)
  const last = refused.map((r) => r.last).sort().at(-1)
  return (
    <div className="grid gap-3">
      <KeyValues
        items={[
          ['Requests a minute', <Limit key="r" limit={limits.requestsPerMinute} used={limits.used?.requests} you={you} />],
          ['Tokens a minute', <Limit key="t" limit={limits.tokensPerMinute} used={limits.used?.tokens} you={you} />],
          ['Requests at once', limits.atOnce === null ? 'no limit' : count(limits.atOnce)],
        ]}
      />
      {total > 0 && (
        <Alert variant="warning">
          {total} request{total === 1 ? '' : 's'} refused in the last day ({refused.map((r) => `${r.count} for ${refusedFor[r.limit] ?? r.limit}`).join(', ')}), the last {ago(last)}.
        </Alert>
      )}
    </div>
  )
}
