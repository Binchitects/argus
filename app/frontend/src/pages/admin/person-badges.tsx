import { Lock, ShieldCheck, UserX } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import type { Person } from './people-api'

export function PersonBadges({ p }: { p: Person }) {
  return (
    <span className="inline-flex flex-wrap gap-1">
      {p.disabled && (
        <Badge variant="destructive">
          <UserX /> Disabled{p.disabledReason === 'ldap' ? ' by directory' : ''}
        </Badge>
      )}
      {p.lockedOut && (
        <Badge variant="warning">
          <Lock /> Locked
        </Badge>
      )}
      {p.twoFactorEnabled && (
        <Badge variant="success">
          <ShieldCheck /> 2FA
        </Badge>
      )}
    </span>
  )
}

/** Spend against credit, as words and a bar. */
export function CreditMeter({ spend, budget }: { spend: number | null; budget: number | null }) {
  const s = spend ?? 0
  const share = budget ? Math.min(1, s / budget) : null
  const fmt = (n: number) => (n !== 0 && n < 0.01 ? `$${Number(n.toPrecision(2))}` : `$${n.toFixed(2)}`)
  return (
    <div className="grid min-w-32 gap-1">
      <span className="text-sm tabular-nums">
        {fmt(s)} <span className="text-muted-foreground">/ {budget === null ? 'no limit' : fmt(budget)}</span>
      </span>
      {share !== null && (
        <span className="h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
          <span className={`block h-full rounded-full ${share >= 1 ? 'bg-destructive' : share >= 0.8 ? 'bg-warning' : 'bg-primary'}`} style={{ width: `${share * 100}%` }} />
        </span>
      )}
    </div>
  )
}
