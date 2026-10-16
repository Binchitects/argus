import { LocateFixed, TextQuote, X } from 'lucide-react'
import { useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Textarea } from '@/components/ui/input'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { QuoteChars, quoteForDisplay, quoteOf, type Quote } from './quote'

/** The quote above the message box: what the reply quotes, and how to take it away. */
export function QuoteChip({ quote, onRemove }: { quote: Quote; onRemove: () => void }) {
  const shown = useMemo(() => quoteForDisplay(quote.text), [quote.text])
  return (
    <fieldset aria-label="Quoted in your reply" className="mx-3 mt-3 flex min-w-0 items-start gap-2 rounded-lg border-s-2 border-primary/60 bg-muted/40 py-1.5 ps-2.5 pe-1 text-sm">
      <TextQuote className="mt-0.5 size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      <span className="grid min-w-0 flex-1">
        <span dir="auto" className="line-clamp-2 break-words text-muted-foreground">
          {shown}
        </span>
        {quote.cut && <span className="text-xs text-muted-foreground">Cut to {QuoteChars.toLocaleString()} characters.</span>}
      </span>
      <Tooltip content="Remove the quote">
        <Button type="button" variant="ghost" size="icon-sm" className="size-6 shrink-0" onClick={onRemove} aria-label="Remove the quote">
          <X className="size-3.5" />
        </Button>
      </Tooltip>
    </fieldset>
  )
}

/**
 * The part of an earlier answer a question quotes, above it: two lines (all of it for a screen reader), Show all when there
 * is more, and a way to the answer it came from.
 */
export function QuotedLine({ quote, onJump }: { quote: string; onJump?: () => boolean }) {
  const shown = useMemo(() => quoteForDisplay(quote), [quote])
  const text = useRef<HTMLQuoteElement>(null)
  const [open, setOpen] = useState(false)
  const [clamped, setClamped] = useState(false)
  useLayoutEffect(() => {
    const el = text.current
    if (el && !open) setClamped(el.scrollHeight > el.clientHeight + 1)
  }, [shown, open])
  return (
    <figure className="flex max-w-[85%] min-w-0 items-start gap-1.5 text-sm text-muted-foreground" aria-label="Quoting an earlier answer">
      <TextQuote className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
      <div className="grid min-w-0">
        <blockquote ref={text} dir="auto" className={cn('border-s-2 ps-2 break-words whitespace-pre-wrap', !open && 'line-clamp-2')}>
          {shown}
        </blockquote>
        <div className="flex flex-wrap gap-x-2">
          {(clamped || open) && (
            <button type="button" className="text-xs underline-offset-2 hover:underline focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none" onClick={() => setOpen(!open)} aria-expanded={open}>
              {open ? 'Show less' : 'Show all'}
            </button>
          )}
          {onJump && (
            <button
              type="button"
              className="inline-flex items-center gap-1 text-xs underline-offset-2 hover:underline focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
              onClick={() => {
                // Not found on screen (another branch, a chat from Code Arena): all of the quote, here.
                if (!onJump()) setOpen(true)
              }}
            >
              <LocateFixed className="size-3" aria-hidden="true" /> Go to the quoted answer
            </button>
          )}
        </div>
      </div>
    </figure>
  )
}

/**
 * Quote part of an answer by keyboard: its Markdown in a read-only box, where Shift and the arrows select; Quote takes the
 * selection (nothing selected: the whole answer, cut to what a message carries).
 */
export function QuotePartDialog({ text, open, onOpenChange, onQuote }: { text: string; open: boolean; onOpenChange: (open: boolean) => void; onQuote: (quote: Quote) => void }) {
  const area = useRef<HTMLTextAreaElement>(null)
  const quoted = useRef(false)
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="sm:max-w-2xl"
        onOpenAutoFocus={(e) => {
          e.preventDefault()
          area.current?.focus()
          area.current?.setSelectionRange(0, 0)
        }}
        // Quoted: the message box takes the focus, not the button that opened this.
        onCloseAutoFocus={(e) => {
          if (quoted.current) e.preventDefault()
          quoted.current = false
        }}
      >
        <DialogHeader>
          <DialogTitle>Quote part of this answer</DialogTitle>
          <DialogDescription>Select the part to quote with Shift and the arrow keys, then press Quote. With nothing selected, the whole answer is quoted.</DialogDescription>
        </DialogHeader>
        <Textarea ref={area} readOnly value={text} dir="auto" aria-label="The answer" className="max-h-[50dvh] min-h-48 font-mono text-sm" />
        <DialogFooter>
          <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            type="button"
            onClick={() => {
              const el = area.current
              const part = el && el.selectionEnd > el.selectionStart ? text.slice(el.selectionStart, el.selectionEnd) : text
              const quote = quoteOf(part)
              if (!quote) return
              quoted.current = true
              onQuote(quote)
              onOpenChange(false)
            }}
          >
            Quote
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
