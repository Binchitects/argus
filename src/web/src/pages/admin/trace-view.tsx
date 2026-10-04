import { useQuery } from '@tanstack/react-query'
import { Bot, Clock, Cpu, Hourglass, Network, Timer, Wrench, type LucideIcon } from 'lucide-react'
import { QueryError } from '@/components/app/query-state'
import { categorical } from '@/components/charts/palette'
import { Badge } from '@/components/ui/badge'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { api } from '@/lib/api'
import { formatValue, when } from '@/lib/format'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { kinds } from '@/pages/chat/context'
import { seconds, toolTitle } from '@/pages/chat/format'
import type { AgentTrace, AnswerTrace, TraceStep, TraceTokens } from './traces-api'

const icons: Record<TraceStep['kind'], LucideIcon> = { queue: Hourglass, setup: Clock, round: Cpu, tool: Wrench, agents: Network }

/** "12.5" → "12.5 a second"; whole above 100. */
const rate = (n: number) => `${n >= 100 ? Math.round(n).toLocaleString() : n.toLocaleString(undefined, { maximumFractionDigits: 1 })} a second`

const percent = (share: number | null | undefined) => (share == null ? null : `${Math.round(share * 100)}%`)

function tokensLine(t: TraceTokens) {
  return [`${formatValue(t.prompt)} in`, t.cacheShare != null && `${percent(t.cacheShare)} from the cache`, `${formatValue(t.completion)} out`].filter(Boolean).join(' · ')
}

/** The engine's read and write speeds, and whether the engine said them or the clock worked them out. */
function speeds(read: number | null, write: number | null, from: string | null) {
  const said = [read != null && `reads ${rate(read)}`, write != null && `writes ${rate(write)}`].filter(Boolean)
  if (!said.length) return null
  return `${said.join(', ')}${from === 'clock' ? ' (worked out from the times)' : ''}`
}

/** A bar for a step's share of the whole answer. */
function Bar({ ms, total, slowest }: { ms: number; total: number; slowest?: boolean }) {
  const width = total > 0 ? Math.max(1, Math.min(100, (ms / total) * 100)) : 0
  return (
    <span className="h-1.5 w-full overflow-hidden rounded-full bg-muted" aria-hidden="true">
      <span className={cn('block h-full rounded-full', slowest ? 'bg-warning' : 'bg-primary/70')} style={{ width: `${width}%` }} />
    </span>
  )
}

/** What a step did, in a line: tokens and speeds for a round, the result's size for a tool. */
function stepDetail(s: TraceStep): string | null {
  switch (s.kind) {
    case 'queue':
      return 'Other answers were being written: the model serves only a few at once.'
    case 'setup':
      return "The chat's tools started and the chat read."
    case 'round':
      return [
        s.tokens && tokensLine(s.tokens),
        s.firstTokenMs != null && `first token after ${seconds(s.firstTokenMs)}`,
        s.thinkingMs && `thought ${seconds(s.thinkingMs)}`,
        speeds(s.readPerSecond, s.writePerSecond, s.speedFrom),
        s.status && s.status !== 'complete' && s.status,
      ].filter(Boolean).join(' · ')
    case 'tool':
    case 'agents':
      return [s.status && s.status !== 'complete' ? s.status : null, s.resultChars != null && `${formatValue(s.resultChars)} characters back`, s.files ? `${s.files} file${s.files === 1 ? '' : 's'}` : null]
        .filter(Boolean)
        .join(' · ')
  }
}

function AgentRow({ a, total }: { a: AgentTrace; total: number }) {
  return (
    <li className={cn('grid gap-1 rounded-md border px-3 py-2', a.slowest && 'border-warning/60 bg-warning/5')} aria-label={a.label}>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Bot className="size-3.5 text-muted-foreground" aria-hidden="true" />
        <span className="font-medium">{a.label}</span>
        {a.slowest && <Badge variant="warning">Slowest</Badge>}
        {a.failed && <Badge variant="destructive">Failed</Badge>}
        <span className="ml-auto text-muted-foreground tabular-nums">{seconds(a.ms)}</span>
      </div>
      <Bar ms={a.ms} total={total} slowest={a.slowest} />
      <p className="text-xs text-muted-foreground tabular-nums">
        {[
          a.model,
          tokensLine(a.tokens),
          a.modelMs != null && `the model ${seconds(a.modelMs)}`,
          speeds(a.readPerSecond, a.writePerSecond, a.speedFrom),
        ].filter(Boolean).join(' · ')}
      </p>
      {a.steps.length > 0 && (
        <ul className="flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-muted-foreground tabular-nums" aria-label={`${a.label}'s tool calls`}>
          {a.steps.map((s, i) => (
            <li key={i} className={cn(s.failed && 'text-destructive-ink')}>
              {toolTitle(s.name)}
              {s.ms != null && ` ${seconds(s.ms)}`}
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

/** The prompt of the first round by part (system, tools, files, the person's messages...), drawn as the context gauge draws it. */
function PromptParts({ parts }: { parts: AnswerTrace['prompt'] }) {
  const { resolved } = useTheme()
  const colors = categorical[resolved]
  const shown = kinds.map((k, i) => ({ ...k, color: colors[i]!, part: parts.find((p) => p.kind === k.key) })).filter((k) => k.part)
  const total = shown.reduce((n, k) => n + (k.part!.tokens ?? k.part!.chars), 0)
  if (!shown.length || total <= 0) return null
  return (
    <section className="grid gap-2" aria-label="The prompt by part">
      <h3 className="text-sm font-medium">The prompt by part (first round)</h3>
      <div className="flex h-2.5 w-full gap-0.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
        {shown.map((k) => (
          <span key={k.key} className="h-full" style={{ width: `${((k.part!.tokens ?? k.part!.chars) / total) * 100}%`, background: k.color }} />
        ))}
      </div>
      <ul className="grid gap-0.5 text-xs sm:grid-cols-2">
        {shown.map((k) => (
          <li key={k.key} className="flex items-center gap-2">
            <span className="size-2 shrink-0 rounded-full" style={{ background: k.color }} aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate">{k.label}</span>
            <span className="text-muted-foreground tabular-nums">{k.part!.tokens != null ? `${formatValue(k.part!.tokens)} tokens` : `${formatValue(k.part!.chars)} characters`}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

/** An answer's trace: its slowest step named, its tokens, its prompt by part, and each step on a timeline. Times, tokens and sizes only. */
export function TraceView({ trace }: { trace: AnswerTrace }) {
  const t = trace
  return (
    <div className="grid gap-5">
      {t.slowest && (
        <section className="rounded-lg border border-warning/50 bg-warning/10 px-4 py-3" aria-label="Slowest step">
          <p className="text-sm">
            <span className="font-medium">Slowest step: {t.slowest.kind === 'tool' ? toolTitle(t.slowest.label) : t.slowest.label}</span>
            <span className="tabular-nums">
              {' '}
              · {seconds(t.slowest.ms)} ({percent(t.slowest.share)} of the answer)
            </span>
          </p>
          {t.slowest.detail && <p className="mt-1 text-sm text-muted-foreground">{t.slowest.detail}</p>}
        </section>
      )}
      <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
        <div>
          <dt className="text-xs text-muted-foreground">Whole answer</dt>
          <dd className="font-semibold tabular-nums">{seconds(t.ms)}</dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Rounds · tool calls</dt>
          <dd className="font-semibold tabular-nums">
            {t.rounds} · {t.toolCalls}
          </dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">Tokens</dt>
          <dd className="font-semibold tabular-nums">
            {formatValue(t.tokens.prompt)} in · {formatValue(t.tokens.completion)} out
          </dd>
        </div>
        <div>
          <dt className="text-xs text-muted-foreground">From the cache</dt>
          <dd className="font-semibold tabular-nums">{percent(t.tokens.cacheShare) ?? '—'}</dd>
        </div>
        {t.agents > 0 && (
          <div className="col-span-2 sm:col-span-4">
            <dt className="text-xs text-muted-foreground">
              {t.agents} sub-agent{t.agents === 1 ? '' : 's'}
            </dt>
            <dd className="font-semibold tabular-nums">{tokensLine(t.agentTokens)}</dd>
          </div>
        )}
      </dl>
      <PromptParts parts={t.prompt} />
      <section className="grid gap-2">
        <h3 className="text-sm font-medium">Step by step</h3>
        <ol className="grid gap-2" aria-label="Timeline">
          {t.steps.map((s, i) => {
            const Icon = icons[s.kind]
            const detail = stepDetail(s)
            return (
              <li key={i} className={cn('grid gap-1.5 rounded-lg border px-3 py-2', s.slowest && 'border-warning/60 bg-warning/5')} aria-label={s.kind === 'tool' ? toolTitle(s.label) : s.label}>
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <Icon className="size-4 text-muted-foreground" aria-hidden="true" />
                  <span className="font-medium">{s.kind === 'tool' ? toolTitle(s.label) : s.label}</span>
                  {s.slowest && <Badge variant="warning">Slowest</Badge>}
                  <span className="ml-auto text-muted-foreground tabular-nums">{seconds(s.ms)}</span>
                </div>
                <Bar ms={s.ms} total={t.ms} slowest={s.slowest} />
                {detail && <p className="text-xs text-muted-foreground tabular-nums">{detail}</p>}
                {s.agents && s.agents.length > 0 && (
                  <ul className="mt-1 grid gap-1.5" aria-label="Sub-agents">
                    {s.agents.map((a) => (
                      <AgentRow key={a.index} a={a} total={t.ms} />
                    ))}
                  </ul>
                )}
              </li>
            )
          })}
        </ol>
      </section>
      <p className="text-xs text-muted-foreground">
        <Timer className="mr-1 inline size-3.5 align-text-bottom" aria-hidden="true" />
        Times, tokens and sizes only: what was asked and answered is not shown here.
      </p>
    </div>
  )
}

/** An answer's trace in a side panel, from the chat (an admin's) or Admin → Traces. */
export function TraceSheet({ answerId, onClose }: { answerId: string | null; onClose: () => void }) {
  const trace = useQuery({
    queryKey: ['admin', 'traces', answerId],
    queryFn: ({ signal }) => api<AnswerTrace>(`/api/admin/traces/${answerId}`, { signal }),
    enabled: !!answerId,
  })
  const t = trace.data
  return (
    <Sheet open={!!answerId} onOpenChange={(open) => !open && onClose()}>
      <SheetContent className="gap-0 sm:max-w-2xl">
        <SheetHeader>
          <SheetTitle>Answer trace</SheetTitle>
          <SheetDescription>
            {t ? [t.person?.displayName || t.person?.userName, t.model, when(t.at)].filter(Boolean).join(' · ') : 'Where the answer’s time went.'}
          </SheetDescription>
        </SheetHeader>
        <div className="min-h-0 flex-1 overflow-y-auto p-5">
          {trace.error ? (
            <QueryError error={trace.error} retry={() => trace.refetch()} />
          ) : t ? (
            <TraceView trace={t} />
          ) : (
            <div className="grid gap-3">
              <Skeleton className="h-16" />
              <Skeleton className="h-10" />
              <Skeleton className="h-40" />
            </div>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}
