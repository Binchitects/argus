import { Check, ChevronRight, ChevronsDownUp, ChevronsUpDown, CircleSlash, CircleX, Clock, Loader2 } from 'lucide-react'
import { Collapsible } from 'radix-ui'
import { useMemo, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { seconds, toolTitle } from './format'
import { blank } from './live'
import { Markdown } from './markdown'
import { Thinking, ToolCard } from './parts'
import type { AgentWork, Message, ToolCall } from './types'

type Part = { title: string; instructions: string }
type Status = 'running' | 'done' | 'failed' | 'stopped' | 'waiting' | 'not run'
type Step = AgentWork['steps'][number]

function statusOf(work: AgentWork | undefined, live: boolean): Status {
  if (!work) return live ? 'waiting' : 'not run'
  const s = work.status ?? (work.error ? 'failed' : 'done')
  // Still at work when the answer is over: it was stopped.
  return s === 'running' && !live ? 'stopped' : s
}

/**
 * Sub-agents at work, one under the other: each part's thinking, tool calls (with what
 * they returned and made) and words, shown as the chat shows its own, live while they
 * run and kept after. A part is open while it works and folds when done, unless the
 * person chose; all open or fold at once. A part not started yet waits its turn (only
 * so many run at once).
 */
export function AgentsView({ parts, agents, live, onOpenFile, onPreview }: {
  parts: Part[]
  agents: (AgentWork | undefined)[]
  live: boolean
  onOpenFile?: (name: string) => void
  onPreview?: (code: string) => void
}) {
  const count = Math.max(parts.length, agents.length)
  const statuses = Array.from({ length: count }, (_, i) => statusOf(agents[i], live))
  const [chosen, setChosen] = useState<Record<number, boolean>>({})
  const isOpen = (i: number) => chosen[i] ?? statuses[i] === 'running'
  const all = (open: boolean) => setChosen(Object.fromEntries(statuses.map((_, i) => [i, open])))
  const tally = (s: Status) => statuses.filter((x) => x === s).length
  const summary = [
    `${tally('done')} of ${count} done`,
    tally('running') && `${tally('running')} working`,
    tally('waiting') && `${tally('waiting')} waiting`,
    tally('failed') && `${tally('failed')} failed`,
    tally('stopped') && `${tally('stopped')} stopped`,
  ].filter(Boolean).join(' · ')
  return (
    <div className="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-2">
      <div className="flex flex-wrap items-center gap-1">
        <p className="min-w-0 flex-1 text-xs text-muted-foreground" aria-live="polite">
          {summary}
        </p>
        <Button variant="ghost" size="sm" className="h-7 text-xs" disabled={statuses.every((_, i) => isOpen(i))} onClick={() => all(true)}>
          <ChevronsUpDown /> Expand all
        </Button>
        <Button variant="ghost" size="sm" className="h-7 text-xs" disabled={statuses.every((_, i) => !isOpen(i))} onClick={() => all(false)}>
          <ChevronsDownUp /> Collapse all
        </Button>
      </div>
      <ol className="grid grid-cols-[minmax(0,1fr)] gap-2" aria-label="Sub-agents">
        {statuses.map((status, i) => (
          <AgentPanel
            key={i}
            index={i}
            part={parts[i]}
            work={agents[i]}
            status={status}
            open={isOpen(i)}
            onOpenChange={(open) => setChosen((c) => ({ ...c, [i]: open }))}
            onOpenFile={onOpenFile}
            onPreview={onPreview}
          />
        ))}
      </ol>
    </div>
  )
}

function StatusIcon({ status }: { status: Status }) {
  if (status === 'running') return <Loader2 className="size-4 shrink-0 animate-spin text-primary-ink" aria-hidden="true" />
  if (status === 'done') return <Check className="size-4 shrink-0 text-success" aria-hidden="true" />
  if (status === 'failed') return <CircleX className="size-4 shrink-0 text-destructive-ink" aria-hidden="true" />
  if (status === 'stopped') return <CircleSlash className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
  return <Clock className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
}

/** What a part is doing now, or what it did: for its folded line. */
function lineOf(status: Status, work: AgentWork | undefined): string {
  const steps = work?.steps ?? []
  if (status === 'waiting') return 'Waiting its turn'
  if (status === 'not run') return 'Not run'
  if (status === 'running') {
    const pending = steps.findLast((s) => s.result === undefined)
    return pending ? `${toolTitle(pending.name)}…` : work?.text ? 'Writing…' : 'Thinking…'
  }
  const made = steps.reduce((n, s) => n + (s.files?.length ?? 0), 0)
  return [
    steps.length && `${steps.length} tool call${steps.length === 1 ? '' : 's'}`,
    made && `${made} file${made === 1 ? '' : 's'}`,
    work?.ms != null && seconds(work.ms),
    work?.model,
    status === 'stopped' && 'Stopped',
  ].filter(Boolean).join(' · ')
}

function AgentPanel({ index, part, work, status, open, onOpenChange, onOpenFile, onPreview }: {
  index: number
  part?: Part
  work?: AgentWork
  status: Status
  open: boolean
  onOpenChange: (open: boolean) => void
  onOpenFile?: (name: string) => void
  onPreview?: (code: string) => void
}) {
  const title = work?.title || part?.title || `Part ${index + 1}`
  const instructions = work?.instructions || part?.instructions
  const running = status === 'running'
  const steps = work?.steps ?? []
  const thinking = running && !work?.text && steps.every((s) => s.result !== undefined)
  return (
    <li aria-label={title} className="min-w-0">
      <Collapsible.Root open={open} onOpenChange={onOpenChange} className={cn('overflow-hidden rounded-lg border bg-card', running && 'border-primary/50')}>
        <Collapsible.Trigger className="group flex w-full min-w-0 items-center gap-2 px-3 py-2 text-left text-sm transition-colors outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:ring-inset">
          <StatusIcon status={status} />
          <span className="shrink-0 text-muted-foreground tabular-nums">{index + 1}.</span>
          <span className="min-w-0 flex-1 truncate font-medium" title={title}>
            {title}
          </span>
          <span className={cn('max-w-[45%] shrink-0 truncate text-xs text-muted-foreground tabular-nums', running && 'text-shimmer')}>{lineOf(status, work)}</span>
          <ChevronRight className="size-3.5 shrink-0 text-muted-foreground transition-transform duration-200 group-data-[state=open]:rotate-90" aria-hidden="true" />
        </Collapsible.Trigger>
        <Collapsible.Content className="overflow-hidden data-[state=closed]:animate-collapsible-up data-[state=open]:animate-collapsible-down">
          <div className="border-t px-3 py-3">
            {instructions && (
              <p dir="auto" className="mb-3 border-s-2 ps-3 text-sm whitespace-pre-wrap text-muted-foreground">
                {instructions}
              </p>
            )}
            {work?.reasoning && <Thinking text={work.reasoning} live={thinking} ms={null} since={null} />}
            {steps.map((s) => (
              <StepCard key={s.id} step={s} live={running} onOpenFile={onOpenFile} />
            ))}
            {work?.text && <Markdown text={work.text} onOpenFile={onOpenFile} onPreview={onPreview} live={running} />}
            {work?.error && <Alert variant="destructive" className="mt-2">{work.error}</Alert>}
            {status === 'waiting' && <p className="text-sm text-muted-foreground">Starts when one before it is done: only so many work at once.</p>}
          </div>
        </Collapsible.Content>
      </Collapsible.Root>
    </li>
  )
}

/** A sub-agent's tool call, as the chat shows its own: the same card, its pictures and files under it. */
function StepCard({ step, live, onOpenFile }: { step: Step; live: boolean; onOpenFile?: (name: string) => void }) {
  const call = useMemo<ToolCall>(() => ({ id: step.id, function: { name: step.name, arguments: step.arguments } }), [step.id, step.name, step.arguments])
  const result = useMemo<Message | undefined>(
    () =>
      step.result === undefined
        ? undefined
        : { ...blank(`${step.id}:result`, 'tool', null, step.result), toolCallId: step.id, toolName: step.name, status: step.isError ? 'failed' : 'complete', attachments: step.files ?? [] },
    [step.id, step.name, step.result, step.isError, step.files],
  )
  return <ToolCard call={call} result={result} live={live} onOpenFile={onOpenFile} />
}
