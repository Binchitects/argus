import { AlertTriangle, Brain, Calculator, Check, ChevronRight, CircleX, Clock, Download, FileText, FolderTree, Globe, Image as ImageIcon, ListTree, Loader2, Network, Search, ShieldQuestion, ShieldX, SquareTerminal, TextSearch, Wrench, type LucideIcon } from 'lucide-react'
import { Collapsible } from 'radix-ui'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { formatValue } from '@/lib/format'
import { attachmentUrl, downloadUrl } from './api'
import { ImageViewer } from './image-viewer'
import { asViewerImages } from './viewer-images'
import { cn } from '@/lib/utils'
import { argsOf, parseResult, resultCount } from './argus'
import { CodeBlock } from './code-block'
import { seconds, toolTitle } from './format'
import { argsSummary, partsOf, partsSummary, splitArgs } from './tool-args'
import { ToolOutput } from './tool-output'
import { AgentsView } from './agents'
import type { ToolRunning } from './live'
import type { AgentWork, Message, ToolCall } from './types'

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
        <div ref={box} dir="auto" className="mt-1 max-h-72 overflow-y-auto border-s-2 ps-4 text-sm leading-relaxed whitespace-pre-wrap text-muted-foreground">
          {text}
        </div>
      </Collapsible.Content>
    </Collapsible.Root>
  )
}

const toolIcons: [RegExp, LucideIcon][] = [
  [/^delegate$/, Network],
  [/python|run_code/, SquareTerminal],
  [/web|url|fetch/, Globe],
  [/image|picture|draw/, ImageIcon],
  [/calculat/, Calculator],
  [/time|date|days_between/, Clock],
  [/symbol|definition|reference/, Search],
  [/grep|search|find/, TextSearch],
  [/read|file|open|show/, FileText],
  [/tree|list|repo/, FolderTree],
  [/outline|structure/, ListTree],
]

/** What a tool was asked: code as code blocks (highlighted, to copy), short values as a list, the rest as JSON. */
function ToolArgsView({ name, raw }: { name: string; raw: string }) {
  const a = useMemo(() => splitArgs(name, raw), [name, raw])
  return (
    <div className="grid gap-2">
      {a.plain.length > 0 && (
        <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs">
          {a.plain.map(([k, v]) => (
            <div key={k} className="contents">
              <dt className="text-muted-foreground">{k}</dt>
              <dd className="font-mono break-all">{v}</dd>
            </div>
          ))}
        </dl>
      )}
      {a.code.map((c) => (
        <CodeBlock key={c.key} code={c.code} lang={c.lang} label={c.lang ? `${c.key} · ${c.lang}` : c.key} />
      ))}
      {a.nested && <CodeBlock code={JSON.stringify(a.nested, null, 2)} lang="json" label="json" />}
      {a.unparsed && <CodeBlock code={a.unparsed} lang={null} label="arguments" />}
    </div>
  )
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
  onOpenFile,
  onPreview,
  progress,
  agents,
}: {
  call: ToolCall
  result?: Message
  live: boolean
  /** While it runs: since when, and how far its server says it is. */
  progress?: ToolRunning
  /** A delegate call's sub-agents, as they work (after: from the result's details). */
  agents?: AgentWork[]
  /** Opens a file the tool made in the Files panel. */
  onOpenFile?: (name: string) => void
  /** Previews code (a sub-agent's words can hold some). */
  onPreview?: (code: string) => void
  /** The call waits for the person to allow it ("ask before running"). */
  waiting?: boolean
  onDecide?: (allow: boolean) => void
}) {
  const delegate = call.function.name === 'delegate'
  const work = agents ?? result?.details?.agents
  // Open while it waits for the person (they read what it would run before they allow it), and
  // while sub-agents work: watched as they worked, it stays open after, unless folded.
  const [chosen, setOpen] = useState<boolean | null>(null)
  const [watched] = useState(() => delegate && !result && live)
  const open = chosen ?? (!!waiting || (delegate && (watched || (!result && live))))
  const [viewing, setViewing] = useState<number | null>(null)
  const pictures = result?.attachments.filter((a) => a.kind === 'image') ?? []
  const made = result?.attachments.filter((a) => a.kind !== 'image') ?? []
  const Icon = toolIcons.find(([re]) => re.test(call.function.name))?.[1] ?? Wrench
  const args: [string, string][] = call.function.name === 'delegate' ? partsSummary(call.function.arguments) : argsSummary(call.function.arguments)
  const running = !result && live && !waiting
  const now = useNow(running && !!progress)
  const ran = running && progress ? now - progress.since : 0
  const share = progress?.total && progress.progress !== undefined ? Math.min(1, Math.max(0, progress.progress / progress.total)) : null
  const declined = result?.status === 'declined'
  const failed = result?.status === 'failed' || declined
  const value = useMemo(() => (result && !declined ? parseResult(result.content) : undefined), [result, declined])
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
                <Loader2 className="size-3.5 animate-spin" aria-hidden="true" /> Running{ran >= 3000 && <span className="tabular-nums"> · {seconds(Math.floor(ran / 1000) * 1000)}</span>}
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
                <Check className="size-3.5 text-success" aria-hidden="true" /> {[count, seconds(result.durationMs)].filter(Boolean).join(' · ')}
              </>
            ) : (
              'Not run'
            )}
            <ChevronRight className="size-3.5 transition-transform duration-200 group-data-[state=open]:rotate-90" aria-hidden="true" />
          </span>
        </Collapsible.Trigger>
        {running && (share !== null || progress?.message) && (
          <div className="flex items-center gap-2 border-t px-3 py-1.5 text-xs text-muted-foreground">
            {share !== null && (
              <progress
                value={Math.round(share * 100)}
                max={100}
                aria-label={`${toolTitle(call.function.name)} progress`}
                className="h-1.5 w-24 shrink-0 appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary [&::-webkit-progress-value]:transition-[width]"
              />
            )}
            {share !== null && <span className="shrink-0 tabular-nums">{Math.round(share * 100)}%</span>}
            {progress?.message && <span className="min-w-0 truncate">{progress.message}</span>}
          </div>
        )}
        <Collapsible.Content className="overflow-hidden data-[state=closed]:animate-collapsible-up data-[state=open]:animate-collapsible-down">
          <div className="grid grid-cols-[minmax(0,1fr)] gap-3 border-t px-3 py-3">
            {delegate && (work?.length || partsOf(call.function.arguments).length) ? (
              <AgentsView parts={partsOf(call.function.arguments)} agents={work ?? []} live={live && !result} onOpenFile={onOpenFile} onPreview={onPreview} />
            ) : null}
            {!delegate && args.length > 0 && (
              <div>
                <p className="mb-1 text-xs font-medium text-muted-foreground">Asked with</p>
                <ToolArgsView name={call.function.name} raw={call.function.arguments} />
              </div>
            )}
            {result && !(delegate && work?.length) && (
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
          <ImageViewer images={asViewerImages(pictures)} index={viewing} onIndex={setViewing} />
        </ul>
      )}
      {made.length > 0 && (
        <ul className="stagger mt-2 flex flex-wrap gap-2" aria-label="Files made">
          {made.map((f) => (
            <li key={f.id} className="flex min-w-0 items-center gap-1 rounded-lg border bg-card py-1 pr-1 pl-2.5 text-sm shadow-xs">
              <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              {f.kind === 'text' && onOpenFile ? (
                <button type="button" onClick={() => onOpenFile(f.fileName)} className="min-w-0 truncate font-medium underline-offset-2 outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring" aria-label={`Open ${f.fileName} in the Files panel`}>
                  {f.fileName}
                </button>
              ) : (
                <span className="min-w-0 truncate font-medium">{f.fileName}</span>
              )}
              <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{formatValue(f.size, 'bytes')}</span>
              <Button asChild variant="ghost" size="icon-sm" className="size-7">
                <a href={downloadUrl(f.id)} download={f.fileName} aria-label={`Download ${f.fileName}`}>
                  <Download />
                </a>
              </Button>
            </li>
          ))}
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
