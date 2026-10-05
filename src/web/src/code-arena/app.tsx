import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Files, GitCompareArrows, Info, MessagesSquare, Search, SquareTerminal, type LucideIcon } from 'lucide-react'
import { lazy, Suspense, useCallback, useEffect, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import { configQuery } from '@/pages/chat/api'
import type { ChatConfig } from '@/pages/chat/types'
import { stateQuery, type CodeEvent, type CodeState } from './api'
import { ChangesPanel } from './changes'
import { Sessions, ThemeMenu, Thread } from './chat'
import { EditorArea, EditorProvider } from './editor'
import { useEditor } from './editor-state'
import { Explorer } from './explorer'
import { changesQuery, parentOf, folderQuery } from './ide-api'
import { SearchPanel } from './search'
import { Splitter } from './splitter'
import { About, StatusBar } from './status-bar'

// xterm.js loads with the panel, the first time it opens.
const TerminalPanel = lazy(() => import('./terminals'))

/**
 * Code Arena in the browser: an editor's workbench around this folder's agent.
 * The activity bar switches the side bar between the Explorer, Search, the
 * agent's changes and the chat's sessions; the editor holds the files' tabs;
 * the terminals sit below it and the chat on the right. Served by code-arena
 * web on 127.0.0.1, behind the key in the address it printed.
 */
export function App() {
  const state = useQuery({ ...stateQuery, refetchInterval: 30_000 })
  const config = useQuery(configQuery)

  useEffect(() => {
    if (state.data) document.title = `${state.data.project} · Code Arena`
  }, [state.data])

  if (state.error || config.error) return <Gone error={state.error ?? config.error} />
  if (!state.data || !config.data)
    return (
      <div className="flex h-dvh flex-col" aria-busy="true" aria-label="Loading">
        <div className="flex min-h-0 flex-1">
          <div className="w-12 border-r bg-sidebar" />
          <div className="hidden w-64 border-r bg-sidebar p-3 md:block">
            {Array.from({ length: 8 }, (_, i) => (
              <Skeleton key={i} className="mb-2 h-4" style={{ width: `${50 + ((i * 37) % 45)}%` }} />
            ))}
          </div>
          <div className="flex-1" />
          <div className="hidden w-96 border-l p-6 lg:block">
            <Skeleton className="mb-4 h-8 w-48" />
            <Skeleton className="h-28 w-full" />
          </div>
        </div>
        <div className="h-6 bg-primary/70" />
      </div>
    )

  return (
    <EditorProvider>
      <Workbench state={state.data} config={config.data} />
    </EditorProvider>
  )
}

/** The server went away (Ctrl+C), or this page holds the key of an earlier run. */
function Gone({ error }: { error: unknown }) {
  const unauthorized = error instanceof ApiError && error.http === 401
  return (
    <div className="mx-auto grid max-w-lg gap-3 px-4 py-[15vh]">
      <h1 className="text-xl font-semibold">Code Arena</h1>
      <Alert variant="warning" title={unauthorized ? 'This page is from an earlier run' : 'code-arena web is not running'}>
        {unauthorized
          ? 'Each run of code-arena web makes a new key. Open the address it printed in your terminal.'
          : 'Start it again in your project (code-arena web) and open the address it prints.'}
      </Alert>
    </div>
  )
}

type View = 'explorer' | 'search' | 'changes' | 'chat'

/** Where the panels are and how large: the person's own, kept in this browser. */
interface Layout {
  view: View
  side: number
  chat: number
  panel: number
  sideOpen: boolean
  chatOpen: boolean
  panelOpen: boolean
}

const layoutKey = 'code-arena:layout'
const defaults: Layout = { view: 'explorer', side: 264, chat: 440, panel: 280, sideOpen: true, chatOpen: true, panelOpen: false }

function useLayout() {
  const [layout, setLayout] = useState<Layout>(() => {
    try {
      return { ...defaults, ...(JSON.parse(localStorage.getItem(layoutKey) ?? '{}') as Partial<Layout>) }
    } catch {
      return defaults
    }
  })
  useEffect(() => {
    try {
      localStorage.setItem(layoutKey, JSON.stringify(layout))
    } catch {
      // A private window: the layout lasts as long as the page.
    }
  }, [layout])
  const change = useCallback((c: Partial<Layout>) => setLayout((l) => ({ ...l, ...c })), [])
  return [layout, change] as const
}

const mod = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘' : 'Ctrl'

const views: { id: View; label: string; icon: LucideIcon; keys?: string }[] = [
  { id: 'explorer', label: 'Explorer', icon: Files, keys: `${mod}+Shift+E` },
  { id: 'search', label: 'Search', icon: Search, keys: `${mod}+Shift+F` },
  { id: 'changes', label: 'Agent changes', icon: GitCompareArrows },
  { id: 'chat', label: 'Chat', icon: MessagesSquare },
]

function ActivityButton({ label, keys, pressed, badge, onClick, children }: { label: string; keys?: string; pressed?: boolean; badge?: number; onClick: () => void; children: ReactNode }) {
  return (
    <Tooltip content={keys ? `${label} (${keys})` : label} side="right">
      <button
        type="button"
        aria-label={badge ? `${label} (${badge})` : label}
        aria-pressed={pressed}
        onClick={onClick}
        className={cn(
          'relative grid size-12 place-items-center text-muted-foreground outline-none transition-colors hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset [&_svg]:size-5',
          pressed && 'text-foreground before:absolute before:inset-y-2 before:left-0 before:w-0.5 before:rounded-r before:bg-primary',
        )}
      >
        {children}
        {!!badge && (
          <span className="absolute right-1.5 bottom-1.5 grid h-4 min-w-4 place-items-center rounded-full bg-primary px-1 text-[0.625rem] leading-none font-semibold text-primary-foreground tabular-nums" aria-hidden="true">
            {badge > 99 ? '99+' : badge}
          </span>
        )}
      </button>
    </Tooltip>
  )
}

function Workbench({ state, config }: { state: CodeState; config: ChatConfig }) {
  const queryClient = useQueryClient()
  const { refresh, setQuickOpen, save, openDiff } = useEditor()
  const [layout, change] = useLayout()
  const [about, setAbout] = useState(false)
  // Bumped to put the focus in the search box or the terminal.
  const [searchFocus, setSearchFocus] = useState(0)
  const [terminalFocus, setTerminalFocus] = useState(0)
  const [panelUsed, setPanelUsed] = useState(layout.panelOpen)
  const changes = useQuery(changesQuery)
  const [height, setHeight] = useState(() => window.innerHeight)

  useEffect(() => {
    const on = () => setHeight(window.innerHeight)
    window.addEventListener('resize', on)
    return () => window.removeEventListener('resize', on)
  }, [])

  /** An activity: its view in the side bar; the one shown already closes the side bar, as in an editor. */
  const show = useCallback(
    (view: View, toggle = false) => {
      if (toggle && layout.sideOpen && layout.view === view) change({ sideOpen: false })
      else change({ view, sideOpen: true, ...(view === 'chat' ? { chatOpen: true } : {}) })
      if (view === 'search') setSearchFocus((n) => n + 1)
    },
    [change, layout.sideOpen, layout.view],
  )

  const togglePanel = useCallback(() => {
    setPanelUsed(true)
    if (!layout.panelOpen) setTerminalFocus((n) => n + 1)
    change({ panelOpen: !layout.panelOpen })
  }, [change, layout.panelOpen])

  // The editor's keys, wherever the focus is (the terminal too, as an editor's terminal lets them through).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const ctrl = e.ctrlKey || e.metaKey
      const key = e.key.toLowerCase()
      let handled = true
      if (ctrl && !e.shiftKey && !e.altKey && key === 's') void save()
      else if (ctrl && !e.shiftKey && !e.altKey && key === 'p') setQuickOpen(true)
      else if (ctrl && e.shiftKey && key === 'f') show('search')
      else if (ctrl && e.shiftKey && key === 'e') show('explorer')
      else if (e.ctrlKey && !e.shiftKey && e.code === 'Backquote') togglePanel()
      else handled = false
      if (handled) {
        e.preventDefault()
        e.stopPropagation()
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [save, setQuickOpen, show, togglePanel])

  // What the agent does shows in the editor: a file it edits reloads (when nothing in it is unsaved), a file it makes appears.
  const onEvent = useCallback(
    (e: CodeEvent) => {
      if (e.type !== 'tool_result' || !e.diff) return
      void refresh(e.diff.path)
      void queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })
      if (e.diff.created) void queryClient.invalidateQueries({ queryKey: folderQuery(parentOf(e.diff.path)).queryKey })
    },
    [queryClient, refresh],
  )
  // A turn over: its commands may have changed files too.
  const onTurnEnd = useCallback(() => void refresh(), [refresh])

  const count = changes.data?.length ?? 0
  const maxPanel = Math.max(160, height - 220)
  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-background text-foreground">
      <div className="flex min-h-0 flex-1">
        <nav aria-label="Activity bar" className="flex w-12 shrink-0 flex-col border-r bg-sidebar">
          {views.map((v) => (
            <ActivityButton key={v.id} label={v.label} keys={v.keys} pressed={layout.sideOpen && layout.view === v.id} badge={v.id === 'changes' ? count : undefined} onClick={() => show(v.id, true)}>
              <v.icon aria-hidden="true" />
            </ActivityButton>
          ))}
          <span className="flex-1" />
          <ActivityButton label="Terminal" keys="Ctrl+`" pressed={layout.panelOpen} onClick={togglePanel}>
            <SquareTerminal aria-hidden="true" />
          </ActivityButton>
          <div className="grid place-items-center pb-1">
            <ThemeMenu side="right" align="end" />
          </div>
          <ActivityButton label="About Code Arena" onClick={() => setAbout(true)}>
            <Info aria-hidden="true" />
          </ActivityButton>
        </nav>

        <aside hidden={!layout.sideOpen} aria-label={views.find((v) => v.id === layout.view)?.label} style={{ width: layout.side }} className="min-h-0 shrink-0 bg-sidebar text-sidebar-foreground">
          <div hidden={layout.view !== 'explorer'} className="h-full">
            <Explorer project={state.project} onOpenChanges={(path) => void openDiff(path)} />
          </div>
          <div hidden={layout.view !== 'search'} className="h-full">
            <SearchPanel focusKey={searchFocus} />
          </div>
          <div hidden={layout.view !== 'changes'} className="h-full">
            <ChangesPanel />
          </div>
          <div hidden={layout.view !== 'chat'} className="h-full">
            <Sessions state={state} />
          </div>
        </aside>
        {layout.sideOpen && <Splitter label="Resize the side bar" orientation="vertical" value={layout.side} min={180} max={640} grow={1} onChange={(side) => change({ side })} />}

        <main className="flex min-w-0 flex-1 flex-col">
          <EditorArea />
          {layout.panelOpen && <Splitter label="Resize the terminal" orientation="horizontal" value={Math.min(layout.panel, maxPanel)} min={100} max={maxPanel} grow={-1} onChange={(panel) => change({ panel })} />}
          {panelUsed && (
            <div hidden={!layout.panelOpen} style={{ height: Math.min(layout.panel, maxPanel) }} className="shrink-0">
              <Suspense
                fallback={
                  <div className="grid h-full place-items-center">
                    <Spinner label="Loading the terminal" />
                  </div>
                }
              >
                <TerminalPanel onHide={() => change({ panelOpen: false })} focusKey={terminalFocus} />
              </Suspense>
            </div>
          )}
        </main>

        {layout.chatOpen && <Splitter label="Resize the chat" orientation="vertical" value={layout.chat} min={320} max={960} grow={-1} onChange={(chat) => change({ chat })} />}
        <aside hidden={!layout.chatOpen} aria-label="Agent" style={{ width: layout.chat }} className="min-h-0 shrink-0">
          <Thread
            key={state.session}
            state={state}
            config={config}
            onOpenList={() => show('chat')}
            onHide={() => change({ chatOpen: false })}
            onEvent={onEvent}
            onTurnEnd={onTurnEnd}
          />
        </aside>
      </div>
      <StatusBar
        state={state}
        onChanges={() => show('changes')}
        onTerminal={togglePanel}
        onChat={() => change({ chatOpen: true })}
        onAbout={() => setAbout(true)}
      />
      <About open={about} onOpenChange={setAbout} state={state} />
    </div>
  )
}
