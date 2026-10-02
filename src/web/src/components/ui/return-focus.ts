import { useRef } from 'react'

/**
 * Where focus goes when a dialog closes: back where it was when the dialog opened.
 * Radix returns focus to its own Trigger only: a dialog opened by a plain button
 * (most of ours) would leave it on the page's body, and a keyboard user back at
 * the top of the page. When that element is gone (the row was deleted), the page's
 * main region takes it.
 */
export function returnFocus(e: Event, opener: Element | null) {
  if (e.defaultPrevented) return
  const target = opener instanceof HTMLElement && opener !== document.body && opener.isConnected ? opener : document.getElementById('main')
  if (!target) return
  e.preventDefault()
  target.focus()
}

// The last element focused outside any dialog. A dialog whose field has autoFocus never
// says it opened (Radix skips the event when focus is inside already); while it is open,
// focus stays in it, so this is still what opened it.
let outside: Element | null = null
document.addEventListener('focusin', (e) => {
  if (e.target instanceof Element && !e.target.closest('[role="dialog"], [role="alertdialog"]')) outside = e.target
})

/** A dialog's open and close focus handlers: what had focus as it opened gets it back as it closes. */
export function useReturnFocus(onOpenAutoFocus?: (e: Event) => void, onCloseAutoFocus?: (e: Event) => void) {
  const opener = useRef<Element | null>(null)
  return {
    onOpenAutoFocus: (e: Event) => {
      // A dialog opened from another dialog: the button in that one.
      opener.current = document.activeElement === document.body ? null : document.activeElement
      onOpenAutoFocus?.(e)
    },
    onCloseAutoFocus: (e: Event) => {
      onCloseAutoFocus?.(e)
      returnFocus(e, opener.current ?? outside)
      opener.current = null
    },
  }
}
