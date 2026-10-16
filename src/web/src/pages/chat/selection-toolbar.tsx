import { Copy, MessageCircleQuestionMark, Reply, SendHorizontal } from 'lucide-react'
import { Toolbar } from 'radix-ui'
import { useMemo, useRef, useState, type RefObject } from 'react'
import { Popover, PopoverAnchor, PopoverContent } from '@/components/ui/popover'
import { toast } from '@/components/ui/toaster'
import { cn } from '@/lib/utils'
import { replyAria, replyKeys, type Quotable } from './quotable'
import type { Quote } from './quote'

const asks = ['Explain this', 'Say more', 'Give an example']

const itemClass =
  'inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-sm whitespace-nowrap outline-none hover:bg-accent hover:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50'

/**
 * The toolbar by a selection in an answer: Reply (the quote goes above the message box), Ask about this (a question about it,
 * sent at once: the box's draft stays), Copy. Above the selection, or below it on a touch screen, where the phone's own menu
 * is not; it follows the thread as it scrolls. It never takes the focus as it opens, and pressing on it keeps the selection.
 */
export function SelectionToolbar({
  at,
  scroller,
  onReply,
  onAsk,
  onClose,
  onHold,
}: {
  at: Quotable | null
  scroller: RefObject<HTMLElement | null>
  onReply: (quote: Quote) => void
  onAsk: (quote: Quote, text: string) => void
  onClose: () => void
  /** While the question field is open, the selection changing (as it takes the focus) does not close the toolbar. */
  onHold: (on: boolean) => void
}) {
  const [asking, setAsking] = useState(false)
  const [question, setQuestion] = useState('')
  const field = useRef<HTMLInputElement>(null)
  const coarse = typeof window !== 'undefined' && !!window.matchMedia?.('(pointer: coarse)').matches
  // Where the selection is now: read again as the thread scrolls (its element is the one watched for that).
  const anchor = useMemo(
    () => ({
      current: {
        getBoundingClientRect: () => (at && typeof at.range.getBoundingClientRect === 'function' ? at.range.getBoundingClientRect() : new DOMRect()),
        get contextElement() {
          return scroller.current ?? undefined
        },
      },
    }),
    [at, scroller],
  )
  // A new selection starts again from the three buttons.
  const [shownFor, setShownFor] = useState(at)
  if (shownFor !== at) {
    setShownFor(at)
    setAsking(false)
    setQuestion('')
  }
  const close = () => {
    onHold(false)
    setAsking(false)
    setQuestion('')
    onClose()
  }
  const ask = (text: string) => {
    if (!at || !text.trim()) return
    onAsk(at.quote, text.trim())
    close()
  }
  return (
    <Popover open={!!at} onOpenChange={(open) => !open && close()} modal={false}>
      <PopoverAnchor virtualRef={anchor} />
      <PopoverContent
        side={coarse ? 'bottom' : 'top'}
        sideOffset={8}
        collisionPadding={8}
        className={cn('w-auto max-w-[calc(100vw-1rem)] p-1 motion-reduce:animate-none', asking && 'w-80')}
        onOpenAutoFocus={(e) => e.preventDefault()}
        onCloseAutoFocus={(e) => e.preventDefault()}
        // Pressing a button keeps the words selected; the question's field takes the press as fields do.
        onMouseDown={(e) => {
          if (!(e.target instanceof HTMLInputElement)) e.preventDefault()
        }}
      >
        {asking && at ? (
          <form
            className="grid gap-1 p-1"
            onSubmit={(e) => {
              e.preventDefault()
              ask(question)
            }}
          >
            <div className="flex items-center gap-1">
              <input
                ref={field}
                value={question}
                onChange={(e) => setQuestion(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Escape') {
                    e.preventDefault()
                    close()
                  }
                }}
                dir="auto"
                aria-label="Your question about the selection"
                placeholder="Ask about this…"
                className="h-8 min-w-0 flex-1 rounded-md border bg-background px-2 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring"
              />
              <button type="submit" className={itemClass} disabled={!question.trim()} aria-label="Ask">
                <SendHorizontal className="size-4" aria-hidden="true" />
              </button>
            </div>
            <fieldset className="flex flex-wrap gap-1" aria-label="Quick questions">
              {asks.map((a) => (
                <button key={a} type="button" className={cn(itemClass, 'h-7 border text-xs')} onClick={() => ask(a)}>
                  {a}
                </button>
              ))}
            </fieldset>
          </form>
        ) : (
          <Toolbar.Root aria-label="The selected words" className="flex items-center gap-0.5">
            <Toolbar.Button className={itemClass} onClick={() => at && onReply(at.quote)} aria-keyshortcuts={replyAria} title={`Reply quoting this (${replyKeys})`}>
              <Reply className="size-4" aria-hidden="true" /> Reply
            </Toolbar.Button>
            <Toolbar.Button
              className={itemClass}
              onClick={() => {
                onHold(true)
                setAsking(true)
                requestAnimationFrame(() => field.current?.focus())
              }}
            >
              <MessageCircleQuestionMark className="size-4" aria-hidden="true" /> Ask about this
            </Toolbar.Button>
            <Toolbar.Separator className="mx-0.5 h-5 w-px bg-border" />
            <Toolbar.Button
              className={itemClass}
              onClick={async () => {
                if (!at) return
                await navigator.clipboard?.writeText(at.quote.words).then(
                  () => toast.success('Copied'),
                  () => toast.error('The browser did not let the page copy: press Ctrl+C.'),
                )
                close()
              }}
            >
              <Copy className="size-4" aria-hidden="true" /> Copy
            </Toolbar.Button>
          </Toolbar.Root>
        )}
      </PopoverContent>
    </Popover>
  )
}
