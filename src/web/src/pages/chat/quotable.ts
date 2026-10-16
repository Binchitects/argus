import { useEffect, useRef, useState, type RefObject } from 'react'
import { quotedIn, quoteOfRange, type Quote } from './quote'

/** A selection in a finished answer that can be quoted: the quote, the answer's message, and where it is on the page. */
export interface Quotable {
  quote: Quote
  source: string
  range: Range
}

/** How long a selection rests before the toolbar shows (dragging the mouse over words changes it all the time). */
const settle = 150

const mac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)

/** Reply's keys, as said to the person and to assistive technology. */
export const replyKeys = mac ? '⌘⇧\'' : "Ctrl+Shift+'"
export const replyAria = mac ? "Meta+Shift+'" : "Control+Shift+'"

/** Ctrl+Shift+' (⌘⇧' on macOS), by the physical key: the same on every keyboard layout. */
export const isReplyKey = (e: Pick<KeyboardEvent, 'metaKey' | 'ctrlKey' | 'shiftKey' | 'altKey' | 'code'>) => (mac ? e.metaKey : e.ctrlKey) && e.shiftKey && !e.altKey && e.code === 'Quote'

/**
 * Watches the page's selection: one inside a finished answer in the scroller (marked `data-quote-source`) is quotable, once
 * it rests. `hold` keeps what is there while the toolbar's own field is typed in (the selection goes as it takes the focus).
 */
export function useQuotableSelection(scroller: RefObject<HTMLElement | null>, sourceOf: (id: string) => string | undefined) {
  const [at, setAt] = useState<Quotable | null>(null)
  const held = useRef(false)
  const source = useRef(sourceOf)
  useEffect(() => {
    source.current = sourceOf
  })
  useEffect(() => {
    let timer = 0
    const read = () => {
      if (held.current) return
      const sel = document.getSelection()
      const box = scroller.current
      if (!sel || sel.isCollapsed || sel.rangeCount === 0 || !box) return setAt(null)
      const range = sel.getRangeAt(0)
      const id = box.contains(range.startContainer) ? quotedIn(range) : null
      const quote = id ? quoteOfRange(range, (x) => source.current(x)) : null
      setAt(id && quote ? { quote, source: id, range } : null)
    }
    const changed = () => {
      window.clearTimeout(timer)
      timer = window.setTimeout(read, settle)
    }
    document.addEventListener('selectionchange', changed)
    return () => {
      window.clearTimeout(timer)
      document.removeEventListener('selectionchange', changed)
    }
  }, [scroller])
  return {
    at,
    /** Closes the toolbar; with `deselect`, the page's selection goes too. */
    clear: (deselect = false) => {
      held.current = false
      setAt(null)
      if (deselect) document.getSelection()?.removeAllRanges()
    },
    hold: (on: boolean) => {
      held.current = on
    },
  }
}
