import { Check, Copy } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import { ScrollRegion } from './scroll-region'

/** Preformatted text with a copy button: commands, .env blocks, logs. */
export function CodeBlock({ code, label, log, className }: { code: string; label?: string; log?: boolean; className?: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className={cn('group relative min-w-0 rounded-lg border bg-muted/50', className)}>
      <ScrollRegion label={label ?? 'Code'} className={cn(log && 'max-h-80')}>
        <pre className={cn('p-3 pr-12 font-mono text-[0.8125rem] leading-relaxed', log && 'text-xs whitespace-pre-wrap')}>{code}</pre>
      </ScrollRegion>
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        className="absolute top-1.5 right-1.5 bg-card/80 opacity-70 group-hover:opacity-100 focus-visible:opacity-100"
        aria-label={copied ? 'Copied' : `Copy ${label ?? 'text'}`}
        onClick={async () => {
          await navigator.clipboard.writeText(code).catch(() => undefined)
          setCopied(true)
          setTimeout(() => setCopied(false), 1500)
        }}
      >
        {copied ? <Check /> : <Copy />}
      </Button>
    </div>
  )
}
