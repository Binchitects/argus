import { Check, ChevronDown, Copy, Download, PanelRightOpen, WrapText } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { extensionFor, highlight } from './highlight'

const COLLAPSE_OVER = 40
const COLLAPSED_LINES = 24

/** A code block: its language or file name, copy, wrap, download, open in the Files panel, line numbers; long ones fold. */
export function CodeBlock({ code, lang, name, onOpen }: { code: string; lang: string | null; name?: string | null; onOpen?: (name: string) => void }) {
  const [wrap, setWrap] = useState(false)
  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(false)
  const text = code.replace(/\n$/, '')
  const lines = text.split('\n')
  const long = lines.length > COLLAPSE_OVER
  const shown = long && !expanded ? lines.slice(0, COLLAPSED_LINES).join('\n') : text
  const html = useMemo(() => highlight(shown, lang), [shown, lang])
  const label = name ?? lang ?? 'text'
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
    <figure className="group/code my-3 min-w-0 overflow-hidden rounded-lg border bg-muted/40 not-first:mt-3" aria-label={`Code: ${label}`}>
      <figcaption className="flex h-9 items-center gap-1 border-b bg-muted/60 pr-1 pl-3 text-xs">
        <span className={cn('truncate font-mono text-muted-foreground', name && 'text-foreground')}>{label}</span>
        <span className="ml-auto flex items-center">
          <Tooltip content={wrap ? 'Do not wrap lines' : 'Wrap lines'}>
            <Button variant="ghost" size="icon-sm" className="size-7" onClick={() => setWrap(!wrap)} aria-label="Wrap lines" aria-pressed={wrap}>
              <WrapText />
            </Button>
          </Tooltip>
          <Tooltip content={`Download ${fileName}`}>
            <Button variant="ghost" size="icon-sm" className="size-7" onClick={download} aria-label={`Download ${fileName}`}>
              <Download />
            </Button>
          </Tooltip>
          {onOpen && name && (
            <Tooltip content="Open in the Files panel">
              <Button variant="ghost" size="icon-sm" className="size-7" onClick={() => onOpen(name)} aria-label={`Open ${name} in the Files panel`}>
                <PanelRightOpen />
              </Button>
            </Tooltip>
          )}
          <Button variant="ghost" size="sm" className="h-7 gap-1 px-2 text-xs" onClick={copy} aria-label={copied ? 'Copied' : 'Copy code'}>
            {copied ? <Check /> : <Copy />} {copied ? 'Copied' : 'Copy'}
          </Button>
        </span>
      </figcaption>
      <div className={cn('relative grid', !wrap && 'grid-cols-[auto_minmax(0,1fr)]')}>
        {!wrap && (
          <div aria-hidden="true" className="border-r py-3 pr-2 pl-3 text-right font-mono text-[0.8125rem] leading-6 text-muted-foreground/70 select-none">
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
      {long && (
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
