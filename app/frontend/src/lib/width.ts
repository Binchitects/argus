import { useSyncExternalStore } from 'react'

/**
 * How much of a wide screen the pages and the chat use: a per-browser choice,
 * applied as <html data-width> (theme-init.js sets it before the first paint).
 * The widths themselves are CSS variables in index.css.
 */
export type WidthPreference = 'comfortable' | 'wide' | 'full'

export const widthLabels: Record<WidthPreference, string> = { comfortable: 'Comfortable', wide: 'Wide', full: 'Full width' }

function read(): WidthPreference {
  try {
    const v = localStorage.getItem('width')
    return v === 'comfortable' || v === 'full' ? v : 'wide'
  } catch {
    return 'wide'
  }
}

const listeners = new Set<() => void>()
let current: WidthPreference = typeof window === 'undefined' ? 'wide' : read()

export function setWidth(w: WidthPreference) {
  current = w
  document.documentElement.dataset.width = w
  try {
    localStorage.setItem('width', w)
  } catch {
    // private window: the choice lasts for this page only
  }
  for (const l of listeners) l()
}

export function useWidth(): WidthPreference {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => current,
  )
}
