import { useCallback, useEffect, useRef } from 'react'
import { reducedMotion } from '@/lib/seeing'

/* The easter eggs' triggers (components/app/games.tsx has the games). */

/** Calls `onDone` after `times` clicks, each within `within` ms of the one before. */
export function useQuickClicks(times: number, onDone: () => void, within = 600) {
  const clicks = useRef<number[]>([])
  return useCallback(() => {
    const now = Date.now()
    const recent = clicks.current.filter((t) => now - t < within * times)
    recent.push(now)
    clicks.current = recent
    if (recent.length >= times) {
      clicks.current = []
      onDone()
    }
  }, [times, onDone, within])
}

const konami = ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a']

/** The old cheat code anywhere on the page (not in a text box): confetti. */
export function useKonami(onDone: () => void) {
  useEffect(() => {
    let at = 0
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null
      if (t && (t.isContentEditable || /^(input|textarea|select)$/i.test(t.tagName))) return
      at = e.key === konami[at] || e.key.toLowerCase() === konami[at] ? at + 1 : e.key === konami[0] ? 1 : 0
      if (at === konami.length) {
        at = 0
        onDone()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onDone])
}

/** A burst of confetti over the page for a few seconds (none for people who asked for less motion). */
export function confetti() {
  // None for people who asked for less motion, nor where elements cannot animate.
  if (reducedMotion() || typeof document.body.animate !== 'function') return
  const layer = document.createElement('div')
  layer.setAttribute('aria-hidden', 'true')
  layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;overflow:hidden;z-index:9999'
  const hues = [210, 38, 145, 340, 265, 190]
  for (let i = 0; i < 120; i++) {
    const p = document.createElement('i')
    const size = 6 + Math.random() * 6
    p.style.cssText = `position:absolute;top:-12px;left:${Math.random() * 100}%;width:${size}px;height:${size * 0.45}px;` +
      `background:hsl(${hues[i % hues.length]} 85% 60%);border-radius:1px;opacity:.95`
    p.animate(
      [
        { transform: `translate(0,0) rotate(0deg)` },
        { transform: `translate(${(Math.random() - 0.5) * 240}px, ${window.innerHeight + 40}px) rotate(${720 * (Math.random() - 0.5)}deg)` },
      ],
      { duration: 1800 + Math.random() * 1600, easing: 'cubic-bezier(.2,.6,.4,1)', delay: Math.random() * 400, fill: 'forwards' },
    )
    layer.appendChild(p)
  }
  document.body.appendChild(layer)
  window.setTimeout(() => layer.remove(), 4200)
}
