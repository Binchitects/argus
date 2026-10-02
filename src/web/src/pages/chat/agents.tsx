import { Brain, Check, ChevronRight, CircleX, Clock, Loader2, Wrench } from 'lucide-react'
import { Collapsible } from 'radix-ui'
import { useEffect, useRef, useState } from 'react'
import { cn } from '@/lib/utils'
import { seconds, toolTitle } from './format'
import { Markdown } from './markdown'
import { argsSummary } from './tool-args'
import type { AgentWork } from './types'

/**
 * Sub-agents at work, side by side: each part's thinking, tool calls (with what they
 * returned) and words, live while they run and kept after. A part not started yet
 * waits its turn (only so many run at once).
 */
export function AgentsView({ parts, agents, live }: { parts: { title: string; instructions: string }[]; agents: (AgentWork | undefined)[]; live: boolean }) {
  const count = Math.max(parts.length, agents.length)
  return (
    <div className="@container">
      <ol className="grid gap-3 @xl:grid-cols-2" aria-label="Sub-agents">
        {Array.from({ length: count }, (_, i) => (
          <AgentPanel key={i} index={i} part={parts[i]} work={agents[i]} live={live} />
        ))}
      </ol>
    </div>
  )
}

function AgentPanel({ index, part, work, live }: { index: number; part?: { title: string; instructions: string }; work?: AgentWork; live: boolean }) {
  const title = work?.title || part?.title || `Part ${index + 1}`
  const status = !work ? (live ? 'waiting' : 'not run') : (work.status ?? (work.error ? 'failed' : 'done'))
  const running = live && status === 'running'
  const [thinking, setThinking] = useState<boolean | null>(null)
  const box = useRef<HTMLDivElement>(null)
  // While it works, its panel follows what it writes.
  useEffect(() => {
    if (running && box.current) box.current.scrollTop = box.current.scrollHeight
  }, [running, work?.reasoning, work?.text, work?.steps.length])
  return (
    <li className={cn('grid min-w-0 content-start overflow-hidden rounded-lg border bg-card', running && 'border-primary/50')} aria-label={title}>
      <div className="flex items-center gap-2 border-b bg-muted/40 px-3 py-2 text-sm">
        {status === 'running' ? (
          <Loader2 className="size-4 shrink-0 animate-spin text-primary-ink" aria-hidden="true" />
        ) : status === 'done' ? (
          <Check className="size-4 shrink-0 text-success" aria-hidden="true" />
        ) : status === 'failed' ? (
          <CircleX className="size-4 shrink-0 text-destructive-ink" aria-hidden="true" />
        ) : (
          <Clock className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        )}
        <span className="min-w-0 flex-1 truncate font-medium" title={title}>
          {title}
        </span>
        <span className="shrink-0 text-xs text-muted-foreground tabular-nums">
          {status === 'waiting' ? 'Waiting its turn' : status === 'not run' ? 'Not run' : `${work?.steps.length ?? 0} tool call${work?.steps.length === 1 ? '' : 's'}${work?.ms != null ? ` · ${seconds(work.ms)}` : ''}`}
        </span>
      </div>
      <div ref={box} className="grid max-h-96 gap-2 overflow-y-auto px-3 py-2 text-sm">
        {(work?.instructions || part?.instructions) && (
          <p dir="auto" className="line-clamp-3 text-xs text-muted-foreground" title={work?.instructions || part?.instructions}>
            {work?.instructions || part?.instructions}
          </p>
        )}
        {work?.reasoning && (
          <Collapsible.Root open={thinking ?? running} onOpenChange={setThinking}>
            <Collapsible.Trigger className="group flex items-center gap-1.5 rounded text-xs text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring">
              <Brain className={cn('size-3.5', running && !work.text && !work.steps.length && 'animate-pulse text-primary-ink')} aria-hidden="true" />
              Thinking
              <ChevronRight className="size-3 transition-transform group-data-[state=open]:rotate-90" aria-hidden="true" />
            </Collapsible.Trigger>
            <Collapsible.Content>
              <p dir="auto" className="mt-1 max-h-40 overflow-y-auto border-s-2 ps-3 text-xs leading-relaxed whitespace-pre-wrap text-muted-foreground">
                {work.reasoning}
              </p>
            </Collapsible.Content>
          </Collapsible.Root>
        )}
        {work?.steps.map((s) => (
          <Step key={s.id} step={s} />
        ))}
        {work?.text && (
          <div className="[&_.md]:text-sm">
            <Markdown text={work.text} />
          </div>
        )}
        {work?.error && <p className="text-xs text-destructive-ink">{work.error}</p>}
      </div>
    </li>
  )
}

function Step({ step }: { step: AgentWork['steps'][number] }) {
  const [open, setOpen] = useState(false)
  const args = argsSummary(step.arguments)
  const done = step.result !== undefined
  return (
    <Collapsible.Root open={open} onOpenChange={setOpen} className="rounded-md border">
      <Collapsible.Trigger className="group flex w-full min-w-0 items-center gap-2 px-2 py-1.5 text-left text-xs outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:ring-inset">
        {done ? (
          step.isError ? <CircleX className="size-3.5 shrink-0 text-destructive-ink" aria-hidden="true" /> : <Wrench className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        ) : (
          <Loader2 className="size-3.5 shrink-0 animate-spin text-primary-ink" aria-hidden="true" />
        )}
        <span className="shrink-0 font-medium">{toolTitle(step.name)}</span>
        <span className="min-w-0 truncate font-mono text-muted-foreground">{args.map(([k, v]) => `${k}: ${v}`).join('  ')}</span>
        <ChevronRight className="ml-auto size-3 shrink-0 transition-transform group-data-[state=open]:rotate-90" aria-hidden="true" />
      </Collapsible.Trigger>
      <Collapsible.Content className="grid gap-1 border-t px-2 py-1.5">
        <pre className="max-h-32 overflow-auto font-mono text-[0.6875rem] whitespace-pre-wrap text-muted-foreground">{step.arguments}</pre>
        {done && <pre className={cn('max-h-48 overflow-auto font-mono text-[0.6875rem] whitespace-pre-wrap', step.isError && 'text-destructive-ink')}>{step.result}</pre>}
      </Collapsible.Content>
    </Collapsible.Root>
  )
}
