import type { LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A headline number: label, value, and a line of context. */
export function Stat({ label, value, hint, icon: Icon, tone, className, text }: { label: string; value: ReactNode; hint?: ReactNode; icon?: LucideIcon; tone?: 'warning' | 'destructive'; className?: string; text?: boolean }) {
  return (
    <div className={cn('min-w-0 rounded-xl border bg-card p-4 shadow-xs', className)}>
      <div className="flex items-center gap-2 text-xs font-medium text-muted-foreground">
        {Icon && <Icon className="size-3.5" aria-hidden="true" />}
        {label}
      </div>
      <div
        className={cn(
          'mt-1.5 font-semibold tabular-nums',
          // A name rather than a number: smaller, and it wraps instead of losing its end.
          text ? 'text-lg leading-snug break-words' : 'truncate text-2xl',
          tone === 'warning' && 'text-warning-ink',
          tone === 'destructive' && 'text-destructive-ink',
        )}
        aria-label={label}
      >
        {value}
      </div>
      {hint && <div className="mt-0.5 truncate text-xs text-muted-foreground">{hint}</div>}
    </div>
  )
}

export function StatGrid({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('stagger grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5', className)}>{children}</div>
}
