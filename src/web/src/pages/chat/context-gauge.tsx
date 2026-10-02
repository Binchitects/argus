import { FoldVertical } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { categorical } from '@/components/charts/palette'
import { formatValue } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { kinds, type ContextView } from './context'

export function ContextGauge({ context, onCompact, busy }: { context: ContextView; onCompact?: () => void; busy: boolean }) {
  const { resolved } = useTheme()
  const colors = categorical[resolved]
  const share = Math.min(1, context.used / context.limit)
  const percent = Math.round(share * 100)
  const free = Math.max(0, context.limit - context.used - (context.output ?? 0))
  const shown = context.parts?.filter((p) => p.tokens > 0) ?? []
  const of = (n: number) => `${Math.max(0, Math.min(100, (n / context.limit) * 100))}%`
  // A ring: full circle 2πr with r = 7.
  const ring = 2 * Math.PI * 7
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" variant="ghost" size="sm" className="h-7 gap-1.5 px-2 text-xs text-muted-foreground tabular-nums" aria-label={`Context: ${percent}% full. Details and compact`}>
          <svg viewBox="0 0 18 18" className="size-4 -rotate-90" aria-hidden="true">
            <circle cx="9" cy="9" r="7" fill="none" strokeWidth="2.5" className="stroke-muted" />
            <circle
              cx="9"
              cy="9"
              r="7"
              fill="none"
              strokeWidth="2.5"
              strokeLinecap="round"
              strokeDasharray={`${share * ring} ${ring}`}
              className={cn(share >= 0.9 ? 'stroke-destructive' : share >= 0.7 ? 'stroke-warning' : 'stroke-primary')}
            />
          </svg>
          {percent}%
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80 max-w-[calc(100vw-1rem)]" aria-label="Context">
        <div className="grid gap-3">
          <div>
            <p className="text-sm font-medium">Context</p>
            <p className="text-xs text-muted-foreground tabular-nums">
              {formatValue(context.used)} of {formatValue(context.limit)} tokens ({percent}%)
            </p>
          </div>
          <div className="flex h-2.5 w-full gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
            {shown.length > 0 ? (
              shown.map((p) => <span key={p.key} className="h-full first:rounded-s-full" style={{ width: of(p.tokens), background: colors[kinds.findIndex((k) => k.key === p.key)] }} />)
            ) : (
              <span className="h-full rounded-s-full bg-primary" style={{ width: of(context.used) }} />
            )}
            {context.output ? <span className="ms-auto h-full bg-[repeating-linear-gradient(135deg,var(--muted-foreground)_0_2px,transparent_2px_5px)] opacity-40" style={{ width: of(context.output) }} /> : null}
          </div>
          {shown.length > 0 ? (
            <ul className="grid gap-1 text-xs" aria-label="What fills it">
              {shown.map((p) => (
                <li key={p.key} className="flex items-center gap-2">
                  <span className="size-2.5 shrink-0 rounded-sm" style={{ background: colors[kinds.findIndex((k) => k.key === p.key)] }} aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate">{p.label}</span>
                  <span className="text-muted-foreground tabular-nums">{formatValue(p.tokens)}</span>
                  <span className="w-10 text-right text-muted-foreground tabular-nums">{Math.round((p.tokens / context.limit) * 100)}%</span>
                </li>
              ))}
              {context.output ? (
                <li className="flex items-center gap-2">
                  <span className="size-2.5 shrink-0 rounded-sm bg-[repeating-linear-gradient(135deg,var(--muted-foreground)_0_2px,transparent_2px_4px)] opacity-60" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate">Kept for the answer</span>
                  <span className="text-muted-foreground tabular-nums">{formatValue(context.output)}</span>
                  <span className="w-10 text-right text-muted-foreground tabular-nums">{Math.round((context.output / context.limit) * 100)}%</span>
                </li>
              ) : null}
              <li className="flex items-center gap-2">
                <span className="size-2.5 shrink-0 rounded-sm border bg-muted" aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate">Free</span>
                <span className="text-muted-foreground tabular-nums">{formatValue(free)}</span>
                <span className="w-10 text-right text-muted-foreground tabular-nums">{Math.round((free / context.limit) * 100)}%</span>
              </li>
            </ul>
          ) : (
            <p className="text-xs text-muted-foreground">What fills it is shown from the next answer on.</p>
          )}
          <p className="text-xs text-muted-foreground">
            Near the limit, the chat compacts itself: its older messages become a summary the model reads instead. Compact now to carry on lighter (also: send /compact).
          </p>
          <Button size="sm" variant="outline" disabled={busy || !onCompact} onClick={onCompact}>
            <FoldVertical /> Compact now
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  )
}
