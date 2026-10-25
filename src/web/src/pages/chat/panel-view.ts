import { useSyncExternalStore } from 'react'

/**
 * Which panel each chat shows in its dialog. Kept beside the page, not in a component: the chat's box (and the Panel
 * button in it) is made anew when a new chat gets its first messages, and the panel goes on showing through that.
 */
let shown: Readonly<Record<string, string>> = {}
const listeners = new Set<() => void>()
const changed = () => listeners.forEach((l) => l())

export const panelView = {
  show(chat: string, panel: string) {
    shown = { ...shown, [chat]: panel }
    changed()
  },
  hide(chat: string) {
    const { [chat]: _gone, ...rest } = shown
    shown = rest
    changed()
  },
  subscribe(listener: () => void) {
    listeners.add(listener)
    return () => void listeners.delete(listener)
  },
}

/** The panel a chat shows in its dialog, if any. */
export function useShownPanel(chat: string | undefined): string | undefined {
  const all = useSyncExternalStore(panelView.subscribe, () => shown)
  return chat ? all[chat] : undefined
}
