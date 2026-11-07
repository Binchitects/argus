import { useQueryClient } from '@tanstack/react-query'
import { SquareArrowDownLeft } from 'lucide-react'
import { useCallback, useEffect, useRef } from 'react'
import { Button } from '@/components/ui/button'
import type { ChatConfig } from '@/pages/chat/types'
import type { CodeState } from './api'
import { Thread } from './chat'
import { useDiskChanges } from './disk-watch'
import { EditorArea } from './editor'
import { useEditor } from './editor-state'
import { nameOf } from './ide-api'
import { useKeyboard } from './keyboard'
import { useSeeingSync, useThemeSync } from './theme'
import { openChannel, type MovedFile, type WindowMessage } from './windows'

// The windows a tab or the chat moved into (windows.ts): an editor with that file, or the chat, and the way back.

/** A window's header: what it shows, and the way back into the IDE. */
function WindowBar({ title, back }: { title: string; back: () => void }) {
  return (
    <header className="flex h-9 shrink-0 items-center gap-2 border-b bg-sidebar px-3 text-sm">
      <span className="min-w-0 flex-1 truncate text-muted-foreground">{title}</span>
      <Button variant="ghost" size="sm" onClick={back}>
        <SquareArrowDownLeft aria-hidden="true" /> Move back into the IDE
      </Button>
    </header>
  )
}

/** An editor tab in a window of its own: it opens the file (with what was unsaved in the IDE), and gives it all back. */
export function DetachedEditor({ path, id, project }: { path: string; id: string; project: string }) {
  const ed = useEditor()
  const queryClient = useQueryClient()
  useThemeSync()
  useSeeingSync()
  useKeyboard()
  const latest = useRef(ed)
  useEffect(() => {
    latest.current = ed
  })
  const sent = useRef(false)
  /** Every file tab, and its text while unsaved, back to the IDE's window. */
  const giveBack = useCallback((ch: BroadcastChannel | null) => {
    if (sent.current || !ch) return
    sent.current = true
    const files = latest.current.tabs.filter((t) => t.kind === 'file' || t.kind === 'media').map((t): MovedFile => ({ path: t.path, text: latest.current.unsavedText(t.path) }))
    if (files.length > 0) ch.postMessage({ type: 'back', kind: 'editor', files } satisfies WindowMessage)
  }, [])
  const channel = useRef<BroadcastChannel | null>(null)
  useEffect(() => {
    const ch = openChannel()
    channel.current = ch
    let opened = false
    const open = async (text?: string) => {
      if (opened) return
      opened = true
      await latest.current.open(path)
      if (text === undefined) return
      const model = await latest.current.modelFor(path)
      if (model && model.getValue() !== text) model.setValue(text)
    }
    if (ch) {
      ch.onmessage = (m: MessageEvent<WindowMessage>) => {
        if (m.data.type === 'take' && m.data.id === id) void open(m.data.text)
      }
      ch.postMessage({ type: 'hello', id, path } satisfies WindowMessage)
    }
    // No IDE window answers (it was closed, or no channel): the file from disk.
    const alone = setTimeout(() => void open(), ch ? 1500 : 0)
    // Closed: what it has goes back to the IDE's window, as VS Code's editors of a closed window do.
    const hide = () => giveBack(ch)
    window.addEventListener('pagehide', hide)
    return () => {
      clearTimeout(alone)
      window.removeEventListener('pagehide', hide)
      ch?.close()
    }
  }, [path, id, giveBack])
  useDiskChanges(
    useCallback(
      (d) => {
        if (!d) return void latest.current.refresh()
        for (const c of d.changes) {
          if (c.path === '') continue
          if (c.kind === 'deleted') latest.current.removed(c.path)
          else void latest.current.refresh(c.path)
        }
        void queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
      },
      [queryClient],
    ),
  )
  useEffect(() => {
    document.title = `${ed.active ? nameOf(ed.active.path) : nameOf(path)} · ${project} · Code Arena`
  }, [ed.active, path, project])
  const back = () => {
    giveBack(channel.current)
    window.close()
  }
  return (
    <div className="flex h-dvh flex-col overflow-clip bg-background text-foreground">
      <WindowBar title={project} back={back} />
      <div className="flex min-h-0 flex-1 flex-col">
        <EditorArea />
      </div>
    </div>
  )
}

/** The chat in a window of its own: the same session; what the agent changes reloads in the IDE's window. */
export function DetachedChat({ state, config }: { state: CodeState; config: ChatConfig }) {
  useThemeSync()
  useSeeingSync()
  useKeyboard()
  const channel = useRef<BroadcastChannel | null>(null)
  const sent = useRef(false)
  useEffect(() => {
    const ch = openChannel()
    channel.current = ch
    const hide = () => {
      if (sent.current) return
      sent.current = true
      ch?.postMessage({ type: 'back', kind: 'chat' } satisfies WindowMessage)
    }
    window.addEventListener('pagehide', hide)
    return () => {
      window.removeEventListener('pagehide', hide)
      ch?.close()
    }
  }, [])
  useEffect(() => {
    document.title = `Chat · ${state.project} · Code Arena`
  }, [state.project])
  const back = () => {
    if (!sent.current) channel.current?.postMessage({ type: 'back', kind: 'chat' } satisfies WindowMessage)
    sent.current = true
    window.close()
  }
  return (
    <div className="flex h-dvh flex-col overflow-clip bg-background text-foreground">
      <WindowBar title={state.project} back={back} />
      <div className="min-h-0 flex-1">
        <Thread
          key={state.session}
          state={state}
          config={config}
          onOpenList={back}
          onEvent={(event) => channel.current?.postMessage({ type: 'event', event } satisfies WindowMessage)}
          onTurnEnd={() => channel.current?.postMessage({ type: 'turn-end' } satisfies WindowMessage)}
        />
      </div>
    </div>
  )
}
