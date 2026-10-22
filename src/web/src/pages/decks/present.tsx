import { ChevronLeft, ChevronRight, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button } from '@/components/ui/button'
import type { DeckTheme, Slide } from './decks-api'
import { SlideView } from './slide-view'

/**
 * Presenting: one slide filling the screen (full screen when the browser allows it), the next on a click, →, Space or
 * Page Down, the one before on ← or Page Up; Escape ends it. Its notes stay off the screen.
 */
export function Present({ slides, theme, start, onClose }: { slides: Slide[]; theme?: DeckTheme | null; start: number; onClose: (at: number) => void }) {
  const [at, setAt] = useState(start)
  const last = slides.length - 1
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (['ArrowRight', 'ArrowDown', 'PageDown', ' ', 'Enter'].includes(e.key)) setAt((n) => Math.min(last, n + 1))
      else if (['ArrowLeft', 'ArrowUp', 'PageUp', 'Backspace'].includes(e.key)) setAt((n) => Math.max(0, n - 1))
      else if (e.key === 'Home') setAt(0)
      else if (e.key === 'End') setAt(last)
      else if (e.key === 'Escape') onClose(at)
      else return
      e.preventDefault()
    }
    // Leaving full screen (Escape, the browser's own button) ends it too.
    const onScreen = () => !document.fullscreenElement && onClose(at)
    window.addEventListener('keydown', onKey)
    document.addEventListener('fullscreenchange', onScreen)
    return () => {
      window.removeEventListener('keydown', onKey)
      document.removeEventListener('fullscreenchange', onScreen)
    }
  }, [at, last, onClose])
  const slide = slides[at]
  if (!slide) return null
  return createPortal(
    <dialog open aria-modal="true" aria-label={`Presenting, slide ${at + 1} of ${slides.length}`} className="fixed inset-0 z-[100] m-0 flex h-full max-h-none w-full max-w-none items-center justify-center border-0 bg-black p-0">
      <button type="button" className="w-[min(100vw,calc(100dvh*16/9))] cursor-default outline-none" aria-label="Next slide" onClick={() => setAt((n) => Math.min(last, n + 1))}>
        <SlideView slide={slide} theme={theme} />
      </button>
      <div className="group absolute inset-x-0 bottom-0 flex h-16 items-center justify-center gap-2 opacity-0 transition-opacity hover:opacity-100 focus-within:opacity-100">
        <Button variant="secondary" size="icon-sm" aria-label="The slide before" onClick={() => setAt((n) => Math.max(0, n - 1))} disabled={at === 0}>
          <ChevronLeft />
        </Button>
        <span className="rounded bg-black/60 px-2 py-1 text-xs text-white tabular-nums">
          {at + 1} / {slides.length}
        </span>
        <Button variant="secondary" size="icon-sm" aria-label="The next slide" onClick={() => setAt((n) => Math.min(last, n + 1))} disabled={at === last}>
          <ChevronRight />
        </Button>
        <Button variant="secondary" size="icon-sm" aria-label="Stop presenting" onClick={() => onClose(at)}>
          <X />
        </Button>
      </div>
    </dialog>,
    document.body,
  )
}

/** Every slide a page of its own, printed (Save as PDF), as on the screen; gone after printing. */
export function DeckPrint({ slides, theme, onDone }: { slides: Slide[]; theme?: DeckTheme | null; onDone: () => void }) {
  useEffect(() => {
    // A moment for the pictures and charts to draw.
    const timer = window.setTimeout(() => window.print(), 600)
    window.addEventListener('afterprint', onDone)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('afterprint', onDone)
    }
  }, [onDone])
  return createPortal(
    <div className="deck-print" aria-hidden="true">
      <style>{'@page { size: 13.333in 7.5in; margin: 0; }'}</style>
      {slides.map((s, i) => (
        <div key={i} className="deck-page">
          <SlideView slide={s} theme={theme} />
        </div>
      ))}
    </div>,
    document.body,
  )
}
