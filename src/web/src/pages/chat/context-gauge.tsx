import { FoldVertical } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { categorical } from '@/components/charts/palette'
import { formatValue } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { compactsText, kindFill, kinds, type CompactSetting, type ContextView } from './context'
import { MessageContextButtons, MessageContextNote, type ContextChange } from './message-context'
import type { ContextItem, Message } from './types'

/** The chat's messages the gauge's biggest items are, and how one is pinned or left out. */
export interface ContextItems {
  find: (id: string) => Message | undefined
  change: (m: Message, change: ContextChange) => void
}

const itemName = (i: ContextItem) => (i.role === 'user' ? 'Your message' : i.role === 'assistant' ? 'An answer' : `${i.tool ?? 'A tool'}'s result`)

export function ContextGauge({ context, onCompact, busy, compact, items }: { context: ContextView; onCompact?: () => void; busy: boolean; compact?: CompactSetting; items?: ContextItems }) {
  const { resolved } = useTheme()
  const colors = categorical[resolved]
  const fillOf = (key: string) => kindFill(kinds.find((k) => k.key === key) ?? { hue: 0 }, colors)
  // The biggest messages still in this chat (a fork's are another chat's): each pinned or left out from here.
  const biggest = (context.biggest ?? []).map((b) => ({ ...b, message: items?.find(b.item.id) })).filter((b) => b.message)
  const share = Math.min(1, context.used / context.limit)
  // The ring warns as the chat nears where it compacts (90% when it compacts only when needed).
  const at = compact && compact.at > 0 ? compact.at / 100 : 0.9
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
              className={cn(share >= at ? 'stroke-destructive' : share >= at - 0.15 ? 'stroke-warning' : 'stroke-primary')}
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
              {context.estimated ? 'About ' : ''}
              {formatValue(context.used)} of {formatValue(context.limit)} tokens ({percent}%)
              {context.estimated ? ' since compacting; the next answer counts it' : ''}
            </p>
          </div>
          <div className="relative flex h-2.5 w-full gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
            {shown.length > 0 ? (
              shown.map((p) => <span key={p.key} className="h-full first:rounded-s-full" style={{ width: of(p.tokens), background: fillOf(p.key) }} />)
            ) : (
              <span className="h-full rounded-s-full bg-primary" style={{ width: of(context.used) }} />
            )}
            {context.output ? <span className="ms-auto h-full bg-[repeating-linear-gradient(135deg,var(--muted-foreground)_0_2px,transparent_2px_5px)] opacity-40" style={{ width: of(context.output) }} /> : null}
            {compact && compact.at > 0 ? <span data-testid="compact-tick" className="absolute inset-y-0 w-0.5 bg-foreground/70" style={{ left: `${compact.at}%` }} /> : null}
          </div>
          {shown.length > 0 ? (
            <ul className="grid gap-1 text-xs" aria-label="What fills it">
              {shown.map((p) => (
                <li key={p.key} className="flex items-center gap-2">
                  <span className="size-2.5 shrink-0 rounded-sm" style={{ background: fillOf(p.key) }} aria-hidden="true" />
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
          {biggest.length > 0 && items ? (
            <div className="grid gap-1">
              <p className="text-xs font-medium">Biggest</p>
              <ul className="grid gap-1 text-xs" aria-label="The biggest messages">
                {biggest.map(({ item, tokens, message }) => (
                  <li key={item.id} className="flex min-w-0 items-center gap-1.5">
                    <span className="min-w-0 flex-1">
                      <span className="block truncate">{itemName(item)}</span>
                      <span className="block truncate text-muted-foreground" title={item.preview}>{message!.leftOut || message!.pinned ? <MessageContextNote m={message!} /> : item.preview}</span>
                    </span>
                    <span className="shrink-0 text-muted-foreground tabular-nums">{formatValue(tokens)}</span>
                    {item.role === 'tool' ? (
                      <MessageContextButtons m={message!} what="result" onChange={items.change} pin={false} />
                    ) : (
                      <MessageContextButtons m={message!} what={item.role === 'user' ? 'question' : 'answer'} onChange={items.change} />
                    )}
                  </li>
                ))}
              </ul>
              <p className="text-xs text-muted-foreground">Pinned: kept word for word when the chat compacts. Left out: not read by the model from the next answer on, and still here.</p>
            </div>
          ) : null}
          <p className="text-xs text-muted-foreground">
            {compact ? compactsText(compact) : 'Near the limit, the chat compacts itself.'} Its older messages then become a summary the model reads instead. Compact now to carry on lighter (also: send /compact).
            {compact?.change ? <> {compact.change}</> : null}
          </p>
          <Button size="sm" variant="outline" disabled={busy || !onCompact} onClick={onCompact}>
            <FoldVertical /> Compact now
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  )
}
