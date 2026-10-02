import { Check, ChevronDown, Code2, Copy, Download, PanelRightOpen, Play, Workflow, WrapText } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { previewLabels, type PreviewKind } from '@/preview/kind'
import { extensionFor, highlight } from './highlight'
import { InlineDiagram } from './live-preview'

const COLLAPSE_OVER = 40
const COLLAPSED_LINES = 24

/**
 * A code block: its language or file name, copy, wrap, download, open in the Files panel, a live preview,
 * line numbers; long ones fold. A Mermaid diagram is drawn in its place, its code a click away.
 */
export function CodeBlock({
  code,
  lang,
  name,
  onOpen,
  preview,
  onPreview,
  label: shownLabel,
  draw = false,
}: {
  code: string
  lang: string | null
  name?: string | null
  /** What the bar says instead of the language (a tool's "Output"); downloads keep a file name. */
  label?: string
  onOpen?: (name: string) => void
  /** What the code can be previewed as, and how to show it. */
  preview?: PreviewKind | null
  onPreview?: () => void
  /** Draw a diagram in place of its code (not while it is still being written). */
  draw?: boolean
}) {
  const [wrap, setWrap] = useState(false)
  const [view, setView] = useState<'diagram' | 'code' | null>(null)
  const [drawError, setDrawError] = useState<string | null>(null)
  const drawable = draw && preview === 'mermaid' && !drawError
  const diagram = drawable && (view ?? 'diagram') === 'diagram'
  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(false)
  const text = code.replace(/\n$/, '')
  const lines = text.split('\n')
  const long = lines.length > COLLAPSE_OVER
  const shown = long && !expanded ? lines.slice(0, COLLAPSED_LINES).join('\n') : text
  const html = useMemo(() => highlight(shown, lang), [shown, lang])
  // html and svg highlight as xml, and jsx as javascript: label them as what they are.
  const label = shownLabel ?? name ?? (preview === 'react' ? (lang === 'typescript' ? 'tsx' : 'jsx') : (preview ?? lang ?? 'text'))
  const fileName = name?.split('/').pop() ?? `snippet.${extensionFor(lang)}`

  const copy = async () => {
    await navigator.clipboard.writeText(text).catch(() => undefined)
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }
  const download = () => {
    const a = document.createElement('a')
    a.href = URL.createObjectURL(new Blob([text], { type: 'text/plain' }))
    a.download = fileName
    a.click()
    URL.revokeObjectURL(a.href)
  }

  return (
    <figure dir="ltr" className="group/code @container my-3 min-w-0 overflow-hidden rounded-lg border bg-muted/40 not-first:mt-3" aria-label={`${diagram ? 'Diagram' : 'Code'}: ${label}`}>
      <figcaption className="flex h-9 items-center gap-1 border-b bg-muted/60 pr-1 pl-3 text-xs">
        <span className={cn('truncate font-mono text-muted-foreground', name && 'text-foreground')}>{label}</span>
        <span className="ml-auto flex items-center">
          {drawable && (
            <Button variant="ghost" size="sm" className="h-7 gap-1 px-2 text-xs" onClick={() => setView(diagram ? 'code' : 'diagram')}>
              {diagram ? <Code2 /> : <Workflow />} {diagram ? 'Code' : 'Diagram'}
            </Button>
          )}
          {!diagram && (
            <Tooltip content={wrap ? 'Do not wrap lines' : 'Wrap lines'}>
              <Button variant="ghost" size="icon-sm" className="size-7" onClick={() => setWrap(!wrap)} aria-label="Wrap lines" aria-pressed={wrap}>
                <WrapText />
              </Button>
            </Tooltip>
          )}
          <Tooltip content={`Download ${fileName}`}>
            <Button variant="ghost" size="icon-sm" className="size-7" onClick={download} aria-label={`Download ${fileName}`}>
              <Download />
            </Button>
          </Tooltip>
          {preview && onPreview && (
            <Tooltip content={`Preview the ${previewLabels[preview]}`}>
              <Button variant="ghost" size="sm" className="h-7 gap-1 px-2 text-xs" onClick={onPreview} aria-label={`Preview ${name ?? `this ${previewLabels[preview]}`}`}>
                <Play /> <span className="hidden @md:inline">Preview</span>
              </Button>
            </Tooltip>
          )}
          {onOpen && name && (
            <Tooltip content="Open in the Files panel">
              <Button variant="ghost" size="icon-sm" className="size-7" onClick={() => onOpen(name)} aria-label={`Open ${name} in the Files panel`}>
                <PanelRightOpen />
              </Button>
            </Tooltip>
          )}
          <Button variant="ghost" size="sm" className="h-7 gap-1 px-2 text-xs" onClick={copy} aria-label={copied ? 'Copied' : 'Copy code'}>
            {copied ? <Check /> : <Copy />} <span className="hidden @md:inline">{copied ? 'Copied' : 'Copy'}</span>
          </Button>
        </span>
      </figcaption>
      {drawError && (
        <div className="grid gap-1 border-b px-3 py-2 text-xs">
          <p className="text-muted-foreground">The diagram could not be drawn:</p>
          <pre className="max-h-32 overflow-auto font-mono whitespace-pre-wrap text-destructive-ink">{drawError.slice(0, 800)}</pre>
        </div>
      )}
      {diagram ? (
        <InlineDiagram code={text} title={accTitle(text) ?? `Diagram: ${label}`} onError={setDrawError} />
      ) : (
      <div className={cn('relative grid', !wrap && 'grid-cols-[auto_minmax(0,1fr)]')}>
        {!wrap && (
          <div aria-hidden="true" className="border-r py-3 pr-2 pl-3 text-right font-mono text-[0.8125rem] leading-6 text-muted-foreground select-none">
            {(long && !expanded ? lines.slice(0, COLLAPSED_LINES) : lines).map((_, i) => (
              <div key={i}>{i + 1}</div>
            ))}
          </div>
        )}
        {/* oxlint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- a code block that scrolls sideways must be scrollable by keyboard */}
        <pre tabIndex={wrap ? undefined : 0} className={cn('overflow-x-auto py-3 pr-4 pl-3 font-mono text-[0.8125rem] leading-6 outline-none focus-visible:ring-[3px] focus-visible:ring-ring', wrap && 'break-words whitespace-pre-wrap')}>
          <code className="hljs" dangerouslySetInnerHTML={{ __html: html }} />
        </pre>
        {long && !expanded && <div aria-hidden="true" className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-gradient-to-t from-muted/90 to-transparent" />}
      </div>
      )}
      {long && !diagram && (
        <button
          type="button"
          onClick={() => setExpanded(!expanded)}
          className="flex w-full items-center justify-center gap-1 border-t py-1.5 text-xs font-medium text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring"
        >
          <ChevronDown className={cn('size-3.5 transition-transform', expanded && 'rotate-180')} aria-hidden="true" />
          {expanded ? 'Show less' : `Show all ${lines.length} lines`}
        </button>
      )}
    </figure>
  )
}

/** A Mermaid diagram's own title for screen readers (accTitle: ...), if it has one. */
function accTitle(code: string): string | null {
  return /^\s*accTitle\s*:\s*(.+)$/m.exec(code)?.[1]?.trim() || null
}
