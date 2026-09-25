import { AlertTriangle, Brain, Calculator, Check, ChevronRight, CircleX, Clock, FileText, FolderTree, Image as ImageIcon, ListTree, Loader2, Search, ShieldQuestion, ShieldX, TextSearch, Wrench, type LucideIcon } from 'lucide-react'
import { Collapsible } from 'radix-ui'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { attachmentUrl } from './api'
import { ImageViewer } from './image-viewer'
import { cn } from '@/lib/utils'
import { argsOf, parseResult, resultCount } from './argus'
import { seconds, toolTitle } from './format'
import { ToolOutput } from './tool-output'
import type { Message, ToolCall } from './types'

function useNow(active: boolean) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!active) return
    const t = setInterval(() => setNow(Date.now()), 250)
    return () => clearInterval(t)
  }, [active])
  return now
}

/** The model's thinking: open and ticking while it thinks, folded to "Thought for 12 s" after. */
export function Thinking({ text, live, ms, since }: { text: string; live: boolean; ms: number | null; since: number | null }) {
  // Open while it thinks and folded after, unless the person chose otherwise.
  const [chosen, setOpen] = useState<boolean | null>(null)
  const open = chosen ?? live
  const box = useRef<HTMLDivElement>(null)
  const now = useNow(live)
  useEffect(() => {
    if (live && box.current) box.current.scrollTop = box.current.scrollHeight
  }, [text, live])
  const label = live ? `Thinking… ${since ? seconds(now - since) : ''}` : ms !== null ? `Thought for ${seconds(ms)}` : 'Thought process'
  return (
    <Collapsible.Root open={open} onOpenChange={setOpen} className="mb-3">
      <Collapsible.Trigger className="group flex items-center gap-1.5 rounded-md py-1 text-sm text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring">
        <Brain className={cn('size-4', live && 'animate-pulse text-primary')} aria-hidden="true" />
        <span className={cn(live && 'text-shimmer')}>{label}</span>
        <ChevronRight className="size-3.5 transition-transform duration-200 group-data-[state=open]:rotate-90" aria-hidden="true" />
      </Collapsible.Trigger>
      <Collapsible.Content className="overflow-hidden data-[state=closed]:animate-collapsible-up data-[state=open]:animate-collapsible-down">
        <div ref={box} className="mt-1 max-h-72 overflow-y-auto border-l-2 pl-4 text-sm leading-relaxed whitespace-pre-wrap text-muted-foreground">
          {text}
        </div>
      </Collapsible.Content>
    </Collapsible.Root>
  )
}

const toolIcons: [RegExp, LucideIcon][] = [
  [/image|picture|draw/, ImageIcon],
  [/calculat/, Calculator],
  [/time|date|days_between/, Clock],
  [/symbol|definition|reference/, Search],
  [/grep|search|find/, TextSearch],
  [/read|file|open|show/, FileText],
  [/tree|list|repo/, FolderTree],
  [/outline|structure/, ListTree],
]

function argsSummary(raw: string): [string, string][] {
  try {
    return Object.entries(JSON.parse(raw) as Record<string, unknown>).map(([k, v]) => [k, typeof v === 'string' ? v : JSON.stringify(v)])
  } catch {
    return raw ? [['arguments', raw]] : []
  }
}

/**
 * One tool call: what was asked, whether it ran, how long it took, what came
 * back. The no-access notice stays outside the fold: the person must see it.
 */
export function ToolCard({
  call,
  result,
  live,
  waiting,
  onDecide,
}: {
  call: ToolCall
  result?: Message
  live: boolean
  /** The call waits for the person to allow it ("ask before running"). */
  waiting?: boolean
  onDecide?: (allow: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const [viewing, setViewing] = useState<number | null>(null)
  const pictures = result?.attachments.filter((a) => a.kind === 'image') ?? []
  const Icon = toolIcons.find(([re]) => re.test(call.function.name))?.[1] ?? Wrench
  const args = argsSummary(call.function.arguments)
  const running = !result && live && !waiting
  const declined = result?.status === 'declined'
  const failed = result?.status === 'failed' || declined
  const value = useMemo(() => (result && !failed ? parseResult(result.content) : undefined), [result, failed])
  const count = resultCount(value)
  return (
    <div className="my-2">
      <Collapsible.Root open={open} onOpenChange={setOpen} className="animate-enter overflow-hidden rounded-lg border bg-card transition-shadow hover:shadow-sm">
        <Collapsible.Trigger className="group flex w-full items-center gap-2.5 px-3 py-2 text-left text-sm transition-colors outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:ring-inset">
          <span className="flex size-6 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary-ink">
            <Icon className="size-3.5" aria-hidden="true" />
          </span>
          <span className="tool-name shrink-0 font-medium">{toolTitle(call.function.name)}</span>
          <span className="min-w-0 truncate text-muted-foreground">
            {args.map(([k, v]) => (
              <span key={k} className="mr-2">
                <span className="text-muted-foreground">{k}:</span> <span className="font-mono text-xs text-foreground">{v}</span>
              </span>
            ))}
          </span>
          <span className="ml-auto flex shrink-0 items-center gap-1.5 text-xs text-muted-foreground">
            {waiting ? (
              <span className="flex items-center gap-1 text-warning-ink">
                <ShieldQuestion className="size-3.5" aria-hidden="true" /> Waiting for you
              </span>
            ) : running ? (
              <>
                <Loader2 className="size-3.5 animate-spin" aria-hidden="true" /> Running
              </>
            ) : declined ? (
              <span className="flex items-center gap-1">
                <ShieldX className="size-3.5" aria-hidden="true" /> Not allowed
              </span>
            ) : failed ? (
              <span className="flex items-center gap-1 text-destructive-ink">
                <CircleX className="size-3.5" aria-hidden="true" /> Failed
              </span>
            ) : result ? (
              <>
                <Check className="size-3.5 text-success" aria-hidden="true" /> {count && <span>{count} ·</span>} {seconds(result.durationMs)}
              </>
            ) : (
              'Not run'
            )}
            <ChevronRight className="size-3.5 transition-transform duration-200 group-data-[state=open]:rotate-90" aria-hidden="true" />
          </span>
        </Collapsible.Trigger>
        <Collapsible.Content className="overflow-hidden data-[state=closed]:animate-collapsible-up data-[state=open]:animate-collapsible-down">
          <div className="grid gap-3 border-t px-3 py-3">
            {args.length > 0 && (
              <div>
                <p className="mb-1 text-xs font-medium text-muted-foreground">Asked with</p>
                <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs">
                  {args.map(([k, v]) => (
                    <div key={k} className="contents">
                      <dt className="text-muted-foreground">{k}</dt>
                      <dd className="font-mono break-all">{v}</dd>
                    </div>
                  ))}
                </dl>
              </div>
            )}
            {result && (
              <div>
                <p className="mb-1 text-xs font-medium text-muted-foreground">{failed ? 'The tool said' : 'Result'}</p>
                <div className="max-h-[32rem] overflow-y-auto text-sm [&_.md]:text-sm">
                  <ToolOutput text={result.content} value={value} args={argsOf(call.function.arguments)} failed={failed} />
                </div>
              </div>
            )}
          </div>
        </Collapsible.Content>
      </Collapsible.Root>
      {waiting && onDecide && (
        <div role="alert" className="mt-2 flex flex-wrap items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2 text-sm">
          <ShieldQuestion className="size-4 shrink-0 text-warning-ink" aria-hidden="true" />
          <span className="min-w-0 flex-1">
            Allow <strong>{toolTitle(call.function.name)}</strong> to run with these arguments?
          </span>
          <Button size="sm" variant="outline" className="h-7" onClick={() => onDecide(false)}>
            Don&apos;t allow
          </Button>
          <Button size="sm" className="h-7" onClick={() => onDecide(true)}>
            Allow
          </Button>
        </div>
      )}
      {pictures.length > 0 && (
        <ul className="mt-2 flex flex-wrap gap-2" aria-label="Pictures">
          {pictures.map((p, i) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => setViewing(i)}
                className="block cursor-zoom-in overflow-hidden rounded-xl border outline-none focus-visible:ring-[3px] focus-visible:ring-ring"
                aria-label={`View ${p.fileName}`}
              >
                <img src={attachmentUrl(p.id)} alt={p.fileName} className="max-h-96 w-auto max-w-full object-contain" loading="lazy" />
              </button>
            </li>
          ))}
          <ImageViewer images={pictures} index={viewing} onIndex={setViewing} />
        </ul>
      )}
      {result?.noAccess && (
        <Alert variant="warning" title="You do not have access to some of this code." className="mt-2" role="note">
          <pre className="mt-1 font-mono text-xs whitespace-pre-wrap">{result.content.split('\n').filter((l) => l.startsWith('- ')).join('\n')}</pre>
          <p className="mt-1">Ask a maintainer listed above to add you in GitLab with at least Reporter access. Argus picks the change up within 10 minutes.</p>
        </Alert>
      )}
    </div>
  )
}

export function NoticeLine({ text }: { text: string }) {
  return (
    <output className="mb-2 flex items-start gap-2 text-sm text-muted-foreground">
      <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
      {text}
    </output>
  )
}
