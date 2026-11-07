import { useCallback, useEffect, useRef } from 'react'
import { toast } from '@/components/ui/toaster'
import type { CodeEvent } from './api'
import type { EditorApi, Tab } from './editor-state'

// A chat or an editor tab moved out of the IDE into a window of its own, as VS Code's "Move into New Window": the IDE's
// page at the same address with ?window=chat or ?window=editor&path=…, the same Code Arena behind it (the session, the
// files, the settings), so both windows show the same. The windows talk over a BroadcastChannel: the new one says it is
// there, the IDE gives it what is unsaved in the file and closes its tab; back (the button, or the window closed), the
// tabs and their unsaved text go back to the IDE's window, and the chat shows there again.

/** What a window of the IDE's is: the IDE itself (null), its chat, or an editor opened with a file. */
export type Detached = { kind: 'chat' } | { kind: 'editor'; path: string; id: string } | null

export function detachedView(search: string = window.location.search): Detached {
  const q = new URLSearchParams(search)
  const w = q.get('window')
  if (w === 'chat') return { kind: 'chat' }
  const path = q.get('path')
  if (w === 'editor' && path) return { kind: 'editor', path, id: q.get('id') ?? path }
  return null
}

/** A file going back to the IDE's window: its path, and its text while unsaved. */
export interface MovedFile {
  path: string
  text?: string
}

export type WindowMessage =
  | { type: 'hello'; id: string; path: string }
  | { type: 'take'; id: string; text?: string }
  | { type: 'back'; kind: 'editor'; files: MovedFile[] }
  | { type: 'back'; kind: 'chat' }
  | { type: 'event'; event: CodeEvent }
  | { type: 'turn-end' }

export const channelName = 'code-arena-windows'

export function openChannel(): BroadcastChannel | null {
  return typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel(channelName)
}

/** The IDE's address with the window's own part: what is there (a Code Studio prefix, a key) stays. */
export function windowUrl(view: Exclude<Detached, null>, href: string = window.location.href): string {
  const url = new URL(href)
  url.searchParams.delete('path')
  url.searchParams.delete('id')
  url.searchParams.set('window', view.kind)
  if (view.kind === 'editor') {
    url.searchParams.set('path', view.path)
    url.searchParams.set('id', view.id)
  }
  url.hash = ''
  return url.toString()
}

const openWindow = (view: Exclude<Detached, null>) =>
  window.open(windowUrl(view), view.kind === 'chat' ? 'code-arena-chat' : `code-arena-${view.id}`, view.kind === 'chat' ? 'popup,width=520,height=820' : 'popup,width=1000,height=760')

const blocked = () => toast.error('The browser kept the new window from opening', { description: 'Allow pop-ups for this page, and try again.' })

/**
 * The IDE's side: it moves a tab or the chat out, gives a new window what it asks for, takes back what comes back,
 * and hears what the agent does in a chat that is elsewhere (a file it edits reloads here).
 */
export function useWindows({ ed, onChatOut, onChatBack, onEvent, onTurnEnd }: { ed: EditorApi; onChatOut: () => void; onChatBack: () => void; onEvent: (e: CodeEvent) => void; onTurnEnd: () => void }) {
  const channel = useRef<BroadcastChannel | null>(null)
  /** The windows made from here, by id: the tab moved there and the api of the group it was in. */
  const out = useRef(new Map<string, { tab: Tab; from: EditorApi; given: boolean }>())
  const latest = useRef({ ed, onChatBack, onEvent, onTurnEnd })
  useEffect(() => {
    latest.current = { ed, onChatBack, onEvent, onTurnEnd }
  })
  useEffect(() => {
    const ch = openChannel()
    channel.current = ch
    if (!ch) return
    ch.onmessage = (m: MessageEvent<WindowMessage>) => {
      const msg = m.data
      const now = latest.current
      if (msg.type === 'hello') {
        const w = out.current.get(msg.id)
        if (!w) return
        // A window reloaded asks again: what is unsaved here now goes with it (the tab came back meanwhile).
        const tab = w.given ? now.ed.tabs.find((t) => t.kind === 'file' && t.path === msg.path) : w.tab
        const from = w.given ? now.ed : w.from
        ch.postMessage({ type: 'take', id: msg.id, text: from.unsavedText(msg.path) } satisfies WindowMessage)
        if (tab) from.release([tab.id])
        w.given = true
      } else if (msg.type === 'back' && msg.kind === 'editor') {
        void (async () => {
          for (const f of msg.files) {
            await now.ed.open(f.path)
            if (f.text === undefined) continue
            const model = await now.ed.modelFor(f.path)
            if (model && model.getValue() !== f.text) model.setValue(f.text)
          }
        })()
      } else if (msg.type === 'back' && msg.kind === 'chat') now.onChatBack()
      else if (msg.type === 'event') now.onEvent(msg.event)
      else if (msg.type === 'turn-end') now.onTurnEnd()
    }
    return () => {
      ch.close()
      channel.current = null
    }
  }, [])

  /** The active tab of the group with the focus into a window of its own. */
  const moveTab = useCallback(() => {
    const now = latest.current.ed
    const tab = now.active
    if (!tab || (tab.kind !== 'file' && tab.kind !== 'media')) return
    const id = crypto.randomUUID()
    out.current.set(id, { tab, from: now, given: false })
    if (!openWindow({ kind: 'editor', path: tab.path, id })) {
      out.current.delete(id)
      blocked()
    }
  }, [])
  /** The chat into a window of its own: hidden here meanwhile. */
  const moveChat = useCallback(() => {
    if (!openWindow({ kind: 'chat' })) return blocked()
    onChatOut()
  }, [onChatOut])
  return { moveTab, moveChat }
}
