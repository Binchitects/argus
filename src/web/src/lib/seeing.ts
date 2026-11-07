import { useSyncExternalStore } from 'react'

/**
 * How the page is made easier to see, for people with low vision or colour blindness: more contrast, larger text, a
 * larger page (its text and everything else), stronger focus rings, less motion and colours told apart without red
 * and green. A per-browser choice (Your account → Appearance), applied as <html data-contrast, data-text, data-scale,
 * data-focus, data-motion, data-colors> (theme-init.js sets them before the first paint); index.css does the rest.
 * Code Arena's IDE sets the same from its own settings while it is open.
 */
export interface Seeing {
  contrast: 'standard' | 'more'
  /** The text's size, in percent of the page's own. */
  text: 100 | 112 | 125 | 150
  /** The whole page's size, its text and everything else, in percent. */
  scale: 100 | 112 | 125 | 150
  focus: 'standard' | 'strong'
  /** "system": as the system asks (prefers-reduced-motion); "reduce": no animation, whatever it asks. */
  motion: 'system' | 'reduce'
  /** "safe": blue and orange where red and green say good and bad (a change added or removed, a check passed or failed). */
  colors: 'standard' | 'safe'
}

export const seeingDefaults: Seeing = { contrast: 'standard', text: 100, scale: 100, focus: 'standard', motion: 'system', colors: 'standard' }

export const sizes = [100, 112, 125, 150] as const

const key = 'seeing'

/** What was saved, what of it makes sense; the defaults for the rest. */
export function seeingOf(saved: unknown): Seeing {
  const s = (saved && typeof saved === 'object' ? saved : {}) as Record<string, unknown>
  const size = (v: unknown) => ((sizes as readonly unknown[]).includes(v) ? (v as Seeing['text']) : 100)
  return {
    contrast: s.contrast === 'more' ? 'more' : 'standard',
    text: size(s.text),
    scale: size(s.scale),
    focus: s.focus === 'strong' ? 'strong' : 'standard',
    motion: s.motion === 'reduce' ? 'reduce' : 'system',
    colors: s.colors === 'safe' ? 'safe' : 'standard',
  }
}

function read(): Seeing {
  try {
    return seeingOf(JSON.parse(localStorage.getItem(key) ?? 'null'))
  } catch {
    return { ...seeingDefaults }
  }
}

/** The page's attributes for these choices: none for a default, so the page is as it always was. */
export function applySeeing(s: Seeing, root: HTMLElement = document.documentElement) {
  const set = (name: 'contrast' | 'text' | 'scale' | 'focus' | 'motion' | 'colors', value: string | null) => {
    if (value === null) delete root.dataset[name]
    else root.dataset[name] = value
  }
  set('contrast', s.contrast === 'more' ? 'more' : null)
  set('text', s.text === 100 ? null : String(s.text))
  set('scale', s.scale === 100 ? null : String(s.scale))
  set('focus', s.focus === 'strong' ? 'strong' : null)
  set('motion', s.motion === 'reduce' ? 'reduce' : null)
  set('colors', s.colors === 'safe' ? 'safe' : null)
}

const listeners = new Set<() => void>()
let current: Seeing = typeof window === 'undefined' ? { ...seeingDefaults } : read()

/** The person's choices on this browser. */
export const savedSeeing = () => current

export function setSeeing(change: Partial<Seeing>) {
  current = { ...current, ...change }
  applySeeing(current)
  try {
    localStorage.setItem(key, JSON.stringify(current))
  } catch {
    // private window: the choice lasts for this page only
  }
  for (const l of listeners) l()
}

export function useSeeing(): Seeing {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => current,
  )
}

/** Whether animation is to be left out: the system asks for less, or the page was told to (data-motion). */
export const reducedMotion = () =>
  document.documentElement.dataset.motion === 'reduce' || !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
