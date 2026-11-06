import { ChevronRight, FileX, FoldVertical, MessageSquareOff, Wrench } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { categorical } from '@/components/charts/palette'
import { formatValue } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { kindFill } from './context'

/** A piece of the window: a tool's definition, a skill, a file, a message, a result. */
export interface WindowItem {
  name: string
  preview?: string | null
  tokens: number
  /** What it came from (a message's id, a tool's id), for the actions on it. */
  ref?: string | null
  tool?: string | null
  /** The person left it out since: the next request does not read it. */
  leftOut?: boolean
}

/** A part of the window (ContextWindow.Parts on the server), with its biggest pieces first. */
export interface WindowPart {
  key: string
  label: string
  tokens: number
  count: number
  items: WindowItem[]
  /** The pieces not listed, and what they hold. */
  more: number
  moreTokens: number
}

/** What could free room, and the action that does it here (null: none from here). */
export interface Freeable {
  kind: 'tool' | 'result' | 'file' | 'compact'
  label: string
  tokens: number
  ref?: string | null
  action?: 'tool-off' | 'leave-out' | 'drop-file' | 'compact' | null
  detail?: string | null
  /** The file to leave out (drop-file). */
  file?: string
}

/** What fills the window, as the server builds it for both apps: adding up to the request's tokens. */
export interface ContextWindowData {
  total: number
  limit: number
  reserve: number
  free: number
  /** Counted with the model's tokenizer, or estimated from characters. */
  counted: 'tokenizer' | 'estimate'
  /** The tokenizer's engine, or why it was estimated. */
  by: string | null
  parts: WindowPart[]
  freeable: Freeable[]
}

/** Each part's place in the categorical palette (8 hues), hatched where two share one. */
const hues: Record<string, { hue: number; hatch?: boolean }> = {
  system: { hue: 0 },
  tools: { hue: 1 },
  instructions: { hue: 2 },
  memory: { hue: 2, hatch: true },
  skills: { hue: 1, hatch: true },
  summary: { hue: 3 },
  files: { hue: 4 },
  you: { hue: 5 },
  answers: { hue: 6 },
  toolResults: { hue: 7 },
  format: { hue: 0, hatch: true },
}

const actionLabels: Record<NonNullable<Freeable['action']>, { label: string; icon: ReactNode }> = {
  'tool-off': { label: 'Turn off', icon: <Wrench /> },
  'leave-out': { label: 'Leave out', icon: <MessageSquareOff /> },
  'drop-file': { label: 'Leave file out', icon: <FileX /> },
  compact: { label: 'Compact now', icon: <FoldVertical /> },
}

const share = (n: number, of: number) => `${Math.round((n / Math.max(1, of)) * 100)}%`

/**
 * What fills the model's window, as Claude Code's /context shows it: a bar of the whole window by part, each part with its
 * tokens and share and, opened, its pieces (each tool's definition, each skill, file and message), the room kept for the
 * answer and what is free, how it was counted, and what could be freed, each with its action where there is one. The web
 * chat's Context panel and Code Arena's show it alike, from the server's figures.
 */
export function ContextWindowView({ window: w, onFree, busy, footer }: { window: ContextWindowData; onFree?: (f: Freeable) => void; busy?: boolean; footer?: ReactNode }) {
  const { resolved } = useTheme()
  const colors = categorical[resolved]
  const fillOf = (key: string) => kindFill(hues[key] ?? { hue: 0 }, colors)
  const [open, setOpen] = useState<string | null>(null)
  const shown = w.parts.filter((p) => p.tokens > 0)
  const of = (n: number) => `${Math.max(0, Math.min(100, (n / Math.max(1, w.limit)) * 100))}%`
  return (
    <div className="grid gap-4 text-sm">
      <div className="grid gap-2">
        <p className="tabular-nums">
          <span className="font-medium">{formatValue(w.total)}</span> of {formatValue(w.limit)} tokens ({share(w.total, w.limit)})
        </p>
        <div className="flex h-3 w-full gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
          {shown.map((p) => (
            <span key={p.key} className="h-full first:rounded-s-full" style={{ width: of(p.tokens), background: fillOf(p.key) }} />
          ))}
          {w.reserve > 0 && <span className="ms-auto h-full bg-[repeating-linear-gradient(135deg,var(--muted-foreground)_0_2px,transparent_2px_5px)] opacity-40" style={{ width: of(w.reserve) }} />}
        </div>
        <p className="text-xs text-muted-foreground">
          {w.counted === 'tokenizer' ? `Counted with the model's own tokenizer (${w.by ?? 'its engine'}).` : `Estimated from characters${w.by ? `: ${w.by}` : ''}.`}
        </p>
      </div>
      <ul className="grid gap-0.5" aria-label="What fills the window">
        {shown.map((p) => {
          const expanded = open === p.key
          const id = `window-part-${p.key}`
          return (
            <li key={p.key} className="grid">
              <button
                type="button"
                className="flex min-w-0 items-center gap-2 rounded-md px-1 py-1 text-start hover:bg-muted/60 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
                aria-expanded={expanded}
                aria-controls={id}
                onClick={() => setOpen(expanded ? null : p.key)}
              >
                <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform', expanded && 'rotate-90')} aria-hidden="true" />
                <span className="size-2.5 shrink-0 rounded-sm" style={{ background: fillOf(p.key) }} aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate">
                  {p.label}
                  {p.count > 1 && <span className="text-muted-foreground"> · {p.count}</span>}
                </span>
                <span className="text-muted-foreground tabular-nums">{formatValue(p.tokens)}</span>
                <span className="w-10 text-right text-muted-foreground tabular-nums">{share(p.tokens, w.limit)}</span>
              </button>
              {expanded && (
                <ul id={id} className="mb-1 ms-6 grid gap-0.5 border-s ps-2 text-xs" aria-label={p.label}>
                  {p.items.map((i, n) => (
                    <li key={`${i.name}-${i.ref ?? ''}-${n}`} className={cn('flex min-w-0 items-center gap-2', i.leftOut && 'text-muted-foreground line-through')}>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate" title={i.name}>
                          {i.name}
                          {i.tool && i.tool !== i.name ? <span className="text-muted-foreground"> ({i.tool})</span> : null}
                        </span>
                        {i.preview ? (
                          <span className="block truncate text-muted-foreground" title={i.preview}>
                            {i.preview}
                          </span>
                        ) : null}
                      </span>
                      {i.leftOut && <span className="sr-only">left out</span>}
                      <span className="shrink-0 text-muted-foreground tabular-nums">{formatValue(i.tokens)}</span>
                    </li>
                  ))}
                  {p.more > 0 && (
                    <li className="text-muted-foreground tabular-nums">
                      and {p.more} more ({formatValue(p.moreTokens)})
                    </li>
                  )}
                  {p.key === 'format' && <li className="text-muted-foreground">The roles, turns and tool markers the model's chat template adds around the pieces.</li>}
                </ul>
              )}
            </li>
          )
        })}
        <li className="flex items-center gap-2 px-1 py-1">
          <span className="size-3.5 shrink-0" aria-hidden="true" />
          <span className="size-2.5 shrink-0 rounded-sm bg-[repeating-linear-gradient(135deg,var(--muted-foreground)_0_2px,transparent_2px_4px)] opacity-60" aria-hidden="true" />
          <span className="min-w-0 flex-1 truncate">Kept for the answer</span>
          <span className="text-muted-foreground tabular-nums">{formatValue(w.reserve)}</span>
          <span className="w-10 text-right text-muted-foreground tabular-nums">{share(w.reserve, w.limit)}</span>
        </li>
        <li className="flex items-center gap-2 px-1 py-1">
          <span className="size-3.5 shrink-0" aria-hidden="true" />
          <span className="size-2.5 shrink-0 rounded-sm border bg-muted" aria-hidden="true" />
          <span className="min-w-0 flex-1 truncate">Free</span>
          <span className="text-muted-foreground tabular-nums">{formatValue(w.free)}</span>
          <span className="w-10 text-right text-muted-foreground tabular-nums">{share(w.free, w.limit)}</span>
        </li>
      </ul>
      {w.freeable.length > 0 && (
        <div className="grid gap-2">
          <p className="font-medium">Could be freed</p>
          <ul className="grid gap-2" aria-label="Could be freed">
            {w.freeable.map((f, n) => (
              <li key={`${f.kind}-${f.ref ?? ''}-${n}`} className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                <span className="min-w-0 flex-1 basis-48">
                  <span className="block truncate" title={f.label}>
                    {f.label}
                  </span>
                  {f.detail && f.detail !== f.label && (
                    <span className="block truncate text-xs text-muted-foreground" title={f.detail}>
                      {f.detail}
                    </span>
                  )}
                </span>
                <span className="shrink-0 text-xs text-muted-foreground tabular-nums">about {formatValue(f.tokens)}</span>
                {f.action && onFree ? (
                  <Button size="sm" variant="outline" className="h-7 shrink-0 text-xs" disabled={busy} onClick={() => onFree(f)} aria-label={`${actionLabels[f.action].label}: ${f.label}`}>
                    {actionLabels[f.action].icon}
                    {actionLabels[f.action].label}
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        </div>
      )}
      {footer}
    </div>
  )
}
