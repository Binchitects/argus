import { useSyncExternalStore } from 'react'

/** A file the Explorer is asked to show: its folders opened, it selected, and scrolled to unless said otherwise. */
export interface Reveal {
  path: string
  scroll: boolean
  /** Each ask its own, so the same file asked again is shown again. */
  at: number
}

let asked: Reveal | null = null
let next = 0
/** The last ask the Explorer did: one asked before it was drawn is done as it is drawn, and none twice. */
let done = 0
const listeners = new Set<() => void>()

/** Asks the Explorer to show this file (Reveal in Explorer View); the Explorer does it when it is drawn, or as it is drawn next. */
export function revealInExplorer(path: string, scroll = true) {
  asked = { path, scroll, at: ++next }
  for (const l of listeners) l()
}

const subscribe = (l: () => void) => {
  listeners.add(l)
  return () => void listeners.delete(l)
}

/** The last file the Explorer was asked to show. */
export const useReveal = () => useSyncExternalStore(subscribe, () => asked)

/** Whether this ask is still to do: true once, then it is done. */
export function takeReveal(r: Reveal) {
  if (r.at <= done) return false
  done = r.at
  return true
}
