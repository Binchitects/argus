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
  // The last selection quotable, kept while the focus is in a field that took it away (the command palette's).
  const recent = useRef<Quotable | null>(null)
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
      const range = sel && !sel.isCollapsed && sel.rangeCount > 0 && box ? sel.getRangeAt(0) : null
      const id = range && box?.contains(range.startContainer) ? quotedIn(range) : null
      const quote = range && id ? quoteOfRange(range, (x) => source.current(x)) : null
      const now = range && id && quote ? { quote, source: id, range } : null
      if (now || !document.activeElement?.matches('input, textarea, [contenteditable="true"]')) recent.current = now
      setAt(now)
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
    /** What was last quotable: still there after the focus went to a field (a command run from the palette quotes it). */
    recent: () => at ?? recent.current,
    clear: (deselect = false) => {
      held.current = false
      recent.current = null
      setAt(null)
      if (deselect) document.getSelection()?.removeAllRanges()
    },
    hold: (on: boolean) => {
      held.current = on
    },
  }
}
