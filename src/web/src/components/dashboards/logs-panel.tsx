import { useState } from 'react'
import { ScrollRegion } from '@/components/app/scroll-region'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { levelOf } from './levels'
import type { LogLine } from './types'

const STEP = 200

const edge = { error: 'border-l-destructive', warn: 'border-l-warning', info: 'border-l-primary/50', debug: 'border-l-muted-foreground/40' }

/**
 * Log lines as Grafana's logs panel shows them: newest first (or oldest), the
 * time, the line wrapped or not, and a line's labels on a click.
 */
export function LogsPanel({ lines, label, showTime = true, showLabels = false, wrap = true, ascending = false, details = true, highlight, tag, className }: {
  lines: LogLine[]
  label: string
  /** A label shown before each line (the container, on the Logs page). */
  tag?: string
  className?: string
  showTime?: boolean
  showLabels?: boolean
  wrap?: boolean
  ascending?: boolean
  details?: boolean
  highlight?: string
}) {
  const [shown, setShown] = useState(STEP)
  const [open, setOpen] = useState<string | null>(null)
  if (!lines.length) return <p className="py-6 text-center text-sm text-muted-foreground">No log lines in this time range.</p>
  const ordered = ascending ? [...lines].reverse() : lines
  // A line's key is its own, not its place: new lines arriving above keep an open line open.
  const seen = new Map<string, number>()
  return (
    <div className="grid gap-2">
      <ScrollRegion label={`${label}, log lines`} className={cn('max-h-[32rem] rounded-lg border bg-muted/20', className)}>
        <ol className="divide-y font-mono text-xs">
          {ordered.slice(0, shown).map((l) => {
            const id = `${l.nanos}:${l.labels.container ?? ''}:${l.line.length}`
            const n = seen.get(id) ?? 0
            seen.set(id, n + 1)
            const key = `${id}:${n}`
            const level = levelOf(l)
            return (
              <li key={key} className={cn('border-l-2 border-l-transparent', level && edge[level])}>
                <button
                  type="button"
                  disabled={!details}
                  onClick={() => setOpen(open === key ? null : key)}
                  aria-expanded={details ? open === key : undefined}
                  className="flex w-full gap-3 px-2 py-1 text-left outline-none hover:bg-accent/50 focus-visible:bg-accent disabled:cursor-text"
                >
                  {showTime && <time className="shrink-0 text-muted-foreground tabular-nums">{new Date(l.time).toLocaleString(undefined, { hour12: false })}</time>}
                  {tag && <span className="w-28 shrink-0 truncate text-primary-ink">{l.labels[tag]}</span>}
                  {showLabels && <span className="shrink-0 text-muted-foreground">{Object.values(l.labels).slice(0, 2).join(' ')}</span>}
                  <span className={cn('min-w-0 text-foreground', wrap ? 'break-all whitespace-pre-wrap' : 'truncate')}>{mark(l.line, highlight)}</span>
                </button>
                {open === key && (
                  <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-0.5 bg-muted/40 px-3 py-2">
                    {Object.entries(l.labels).map(([k, v]) => (
                      <div key={k} className="contents">
                        <dt className="text-muted-foreground">{k}</dt>
                        <dd className="break-all">{v}</dd>
                      </div>
                    ))}
                  </dl>
                )}
              </li>
            )
          })}
        </ol>
      </ScrollRegion>
      <div className="flex items-center justify-between text-xs text-muted-foreground">
        <span>
          {Math.min(shown, lines.length)} of {lines.length} lines
        </span>
        {shown < lines.length && (
          <Button variant="ghost" size="sm" className="h-7" onClick={() => setShown(shown + STEP)}>
            Show more
          </Button>
        )}
      </div>
    </div>
  )
}

/** The searched-for text, marked where it appears. */
function mark(text: string, needle?: string) {
  if (!needle) return text
  const lower = text.toLowerCase()
  const n = needle.toLowerCase()
  const parts: (string | React.ReactElement)[] = []
  let at = 0
  for (let i = lower.indexOf(n); i >= 0 && n; i = lower.indexOf(n, i + n.length)) {
    parts.push(text.slice(at, i), <mark key={i} className="rounded-sm bg-warning/40 text-foreground">{text.slice(i, i + n.length)}</mark>)
    at = i + n.length
  }
  parts.push(text.slice(at))
  return parts
}
