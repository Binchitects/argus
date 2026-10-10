import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, CircleAlert, CircleHelp, Files, GitBranch, GitCompareArrows, Info, MessagesSquare, Search, SquareTerminal, type LucideIcon } from 'lucide-react'
import { lazy, Suspense, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError } from '@/lib/api'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { configQuery } from '@/pages/chat/api'
import type { ChatConfig } from '@/pages/chat/types'
import { stateQuery, type CodeEvent, type CodeState } from './api'
import { ChangesPanel } from './changes'
import { Sessions, ThemeMenu, Thread } from './chat'
import { completion } from './completion'
import { CommandPalette, type PaletteCommand } from './palette'
import { ProblemsPanel } from './problems'
import { SourceControlPanel } from './scm'
import { EditorArea, EditorProvider } from './editor'
import { CodeRefsProvider } from './refs'
import { modKey, onMac, useEditor } from './editor-state'
import { Explorer } from './explorer'
import { HelpProvider } from './help'
import { useOpenHelp } from './help-context'
import { changesQuery, folderQuery, gitStatusQuery, parentOf, preferencesQuery, problemsQuery, savePreferences, type Preferences } from './ide-api'
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
  // Read again every 30 seconds; every 5 while an MCP server is connecting or down (the status bar says when it connects),
  // or while the session is kept with a chat in Arena (what the web adds shows within seconds).
  const state = useQuery({
    ...stateQuery,
    // Every second while a command with no time limit runs (its output comes from here once the turn that started it has
    // ended); every 2 while a server runs in the background, messages wait in the queue, or a turn runs that the page did
    // not start (a command ended: the model carries on).
    refetchInterval: (q) =>
      q.state.data?.jobs.some((j) => j.running && !j.background)
        ? 1_000
        : (q.state.data?.queued?.length ?? 0) > 0 || q.state.data?.busy || q.state.data?.jobs.some((j) => j.running)
          ? 2_000
          : q.state.data?.chat || q.state.data?.servers.some((s) => s.state === 'connecting' || s.state === 'failed')
            ? 5_000
            : 30_000,
  })
  const config = useQuery(configQuery)
  // The layout and the theme as they were left, in any run: waited for, so the workbench opens as it was.
  const preferences = useQuery(preferencesQuery)
  useSavedTheme(preferences.data)

  useEffect(() => {
    if (state.data) document.title = `${state.data.project} · Code Arena`
  }, [state.data])

  // Gone only before anything loaded: once the workbench is there, it stays (with what is not saved in it) and says what is wrong.
  if ((state.error && !state.data) || (config.error && !config.data)) return <Gone error={state.error ?? config.error} />
  if (!state.data || !config.data || preferences.isPending)
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
      <HelpProvider manual={state.data.manual}>
        <CodeRefsProvider folder={state.data.folder}>
          <Workbench state={state.data} config={config.data} lost={state.error ?? config.error} saved={preferences.data?.layout} completingSaved={preferences.data?.completion !== false} />
        </CodeRefsProvider>
      </HelpProvider>
    </EditorProvider>
  )
}

/** The theme last picked, in whichever run: applied once, as the page opens (this port's browser storage may hold none, or an older one). */
function useSavedTheme(saved: Preferences | undefined) {
  const { setPreference } = useTheme()
  const applied = useRef(false)
  useEffect(() => {
    if (applied.current || !saved) return
    applied.current = true
    if (saved.theme === 'light' || saved.theme === 'dark' || saved.theme === 'system') setPreference(saved.theme)
  }, [saved, setPreference])
}

const earlierRun = (error: unknown) => error instanceof ApiError && error.http === 401

/** The server went away (Ctrl+C), or this page holds the key of an earlier run. */
function Gone({ error }: { error: unknown }) {
  const unauthorized = earlierRun(error)
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

/**
 * The server stopped answering after the page loaded (it stopped, or the SSH
 * tunnel to it dropped): the workbench stays, so what is not saved can still be
 * copied out, and the page keeps asking; this goes when it answers again.
 */
function Unreachable({ error }: { error: unknown }) {
  const unauthorized = earlierRun(error)
  return (
    <div role="alert" className="flex shrink-0 items-start gap-2 border-b border-warning/40 bg-warning/10 px-3 py-1.5 text-sm">
      <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
      <p className="min-w-0">
        <span className="font-medium">{unauthorized ? 'This page is from an earlier run of code-arena.' : 'code-arena web is not answering.'}</span>{' '}
        <span className="text-muted-foreground">
          {unauthorized ? 'Open the address it printed in your terminal.' : 'It stopped, or the connection to it dropped: this page keeps trying.'} What is not saved stays in the editor until this page
          closes: copy it out before you close it.
        </span>
      </p>
    </div>
  )
}

type View = 'explorer' | 'search' | 'git' | 'changes' | 'problems' | 'chat'

/** Where the panels are and how large: the person's own, kept by code-arena for every run. */
interface Layout {
  view: View
  side: number
  chat: number
  panel: number
  sideOpen: boolean
  chatOpen: boolean
  panelOpen: boolean
}

const defaults: Layout = { view: 'explorer', side: 264, chat: 440, panel: 280, sideOpen: true, chatOpen: true, panelOpen: false }
/** The panels' sizes, smallest and largest (the terminal's largest also follows the window). */
const bounds = { side: [180, 640], chat: [320, 960], panel: [100, 4000] } as const

/** The layout saved, what of it still makes sense: the known keys, of their kind, the sizes within bounds. */
function layoutOf(saved: Record<string, unknown> | undefined): Layout {
  const layout = { ...defaults }
  if (!saved) return layout
  const view = saved.view
  if (view === 'explorer' || view === 'search' || view === 'git' || view === 'changes' || view === 'problems' || view === 'chat') layout.view = view
  for (const k of ['side', 'chat', 'panel'] as const) {
    const size = saved[k]
    if (typeof size === 'number' && Number.isFinite(size)) layout[k] = Math.round(Math.min(bounds[k][1], Math.max(bounds[k][0], size)))
  }
  for (const k of ['sideOpen', 'chatOpen', 'panelOpen'] as const) {
    const open = saved[k]
    if (typeof open === 'boolean') layout[k] = open
  }
  return layout
}

function useLayout(saved: Record<string, unknown> | undefined) {
  const [layout, setLayout] = useState<Layout>(() => layoutOf(saved))
  // Kept by code-arena (a page's browser storage is per port, and each run takes a new one): a moment after the
  // last change, as a drag changes it at every step, or as the page closes.
  const kept = useRef(JSON.stringify(layout))
  useEffect(() => {
    const json = JSON.stringify(layout)
    if (json === kept.current) return
    const keep = (keepalive: boolean) => {
      kept.current = json
      void savePreferences({ layout: { ...layout } }, keepalive).catch(() => undefined)
    }
    const soon = setTimeout(() => keep(false), 400)
    const leaving = () => {
      clearTimeout(soon)
      keep(true)
    }
    window.addEventListener('pagehide', leaving)
    return () => {
      clearTimeout(soon)
      window.removeEventListener('pagehide', leaving)
    }
  }, [layout])
  const change = useCallback((c: Partial<Layout>) => setLayout((l) => ({ ...l, ...c })), [])
  return [layout, change] as const
}

const views: { id: View; label: string; icon: LucideIcon; keys?: string }[] = [
  { id: 'explorer', label: 'Explorer', icon: Files, keys: `${modKey}+Shift+E` },
  { id: 'search', label: 'Search', icon: Search, keys: `${modKey}+Shift+F` },
  { id: 'git', label: 'Source control', icon: GitBranch, keys: `${modKey}+Shift+G` },
  { id: 'changes', label: 'Agent changes', icon: GitCompareArrows },
  { id: 'problems', label: 'Problems', icon: CircleAlert },
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

function Workbench({
  state,
  config,
  lost,
  saved,
  completingSaved,
}: {
  state: CodeState
  config: ChatConfig
  lost: unknown
  saved: Record<string, unknown> | undefined
  completingSaved: boolean
}) {
  const queryClient = useQueryClient()
  const { refresh, setQuickOpen, save, openDiff, accept, editorCommands, runAction, showProblems, active } = useEditor()
  const [palette, setPalette] = useState(false)
  // Code completion: the person's choice (kept with the layout), and not while the agent is working.
  const [completing, setCompleting] = useState(completingSaved)
  useEffect(() => {
    const on = !!state.completion && completing && !state.busy
    completion.wanted = () => on
  }, [state.completion, state.busy, completing])
  const [layout, change] = useLayout(saved)
  // The editor sent lines to the chat: it shows, if hidden.
  const showChat = useCallback(() => change({ chatOpen: true }), [change])
  const [about, setAbout] = useState(false)
  const openHelp = useOpenHelp()
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
  // On macOS they are ⌘: Ctrl+S, Ctrl+P… stay the terminal's and the editor's own there.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const ctrl = onMac() ? e.metaKey && !e.ctrlKey : e.ctrlKey && !e.metaKey
      const key = e.key.toLowerCase()
      let handled = true
      if (ctrl && !e.shiftKey && !e.altKey && key === 's') void save()
      else if (ctrl && !e.shiftKey && !e.altKey && key === 'p') setQuickOpen(true)
      else if (ctrl && e.shiftKey && !e.altKey && key === 'p') setPalette(true)
      else if (ctrl && e.shiftKey && key === 'f') show('search')
      else if (ctrl && e.shiftKey && key === 'e') show('explorer')
      else if (ctrl && e.shiftKey && key === 'g') show('git')
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
  // The check's problems: marked in the editor's files, counted on the activity bar and the status bar.
  const problems = useQuery(problemsQuery).data?.problems
  const errors = problems?.filter((p) => p.severity === 'error').length ?? 0
  const gitCount = useQuery(gitStatusQuery).data?.files?.length ?? 0
  useEffect(() => {
    if (problems) showProblems(problems)
  }, [problems, showProblems])
  const maxPanel = Math.min(bounds.panel[1], Math.max(160, height - 220))
  const { setPreference } = useTheme()
  // Ctrl+Shift+P: the workbench's commands, then the editor's (while a file is shown).
  const commands = (): PaletteCommand[] => {
    const file = active?.kind === 'file' && active.status === 'ready'
    const workbench: (PaletteCommand | false)[] = [
      { id: 'quick-open', label: 'Go to file', keys: `${modKey}+P`, run: () => setQuickOpen(true) },
      file && { id: 'go-to-line', label: 'Go to line', run: () => setQuickOpen(true, ':') },
      file && { id: 'save', label: 'Save the file', keys: `${modKey}+S`, run: () => void save() },
      { id: 'search', label: 'Search the files', keys: `${modKey}+Shift+F`, run: () => show('search') },
      { id: 'explorer', label: 'Show the Explorer', keys: `${modKey}+Shift+E`, run: () => show('explorer') },
      { id: 'changes', label: "Show the agent's changes", run: () => show('changes') },
      { id: 'problems', label: 'Show the problems', run: () => show('problems') },
      { id: 'git', label: 'Show source control (git)', keys: `${modKey}+Shift+G`, run: () => show('git') },
      count > 0 && { id: 'accept-all', label: "Accept all the agent's changes", run: () => void accept() },
      { id: 'sessions', label: 'Show the chat sessions', run: () => show('chat') },
      { id: 'chat', label: layout.chatOpen ? 'Hide the chat' : 'Show the chat', run: () => change({ chatOpen: !layout.chatOpen }) },
      { id: 'side', label: layout.sideOpen ? 'Hide the side bar' : 'Show the side bar', run: () => change({ sideOpen: !layout.sideOpen }) },
      { id: 'terminal', label: layout.panelOpen ? 'Hide the terminal' : 'Show the terminal', keys: 'Ctrl+`', run: togglePanel },
      { id: 'theme-light', label: 'Theme: light', run: () => setPreference('light') },
      { id: 'theme-dark', label: 'Theme: dark', run: () => setPreference('dark') },
      { id: 'theme-system', label: "Theme: the system's", run: () => setPreference('system') },
      { id: 'help', label: 'Help', run: () => openHelp?.() },
      { id: 'about', label: 'About Code Arena', run: () => setAbout(true) },
    ]
    const own = new Set(['arena.addToChat', 'arena.explain', 'arena.fix', 'arena.complete'])
    const editor = editorCommands().map((c): PaletteCommand => ({ id: `editor:${c.id}`, label: own.has(c.id) ? c.label : `Editor: ${c.label}`, keys: c.id === 'arena.addToChat' ? `${modKey}+L` : undefined, run: () => runAction(c.id) }))
    return [...workbench.filter((c): c is PaletteCommand => !!c), ...editor.sort((a, b) => Number(!a.label.startsWith('Code Arena')) - Number(!b.label.startsWith('Code Arena')) || a.label.localeCompare(b.label))]
  }
  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-background text-foreground">
      {!!lost && <Unreachable error={lost} />}
      <div className="flex min-h-0 flex-1">
        <nav aria-label="Activity bar" className="flex w-12 shrink-0 flex-col border-r bg-sidebar">
          {views.map((v) => (
            <ActivityButton key={v.id} label={v.label} keys={v.keys} pressed={layout.sideOpen && layout.view === v.id} badge={v.id === 'changes' ? count : v.id === 'problems' ? errors : v.id === 'git' ? gitCount : undefined} onClick={() => show(v.id, true)}>
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
          <ActivityButton label="Help" onClick={() => openHelp?.()}>
            <CircleHelp aria-hidden="true" />
          </ActivityButton>
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
          <div hidden={layout.view !== 'git'} className="h-full">
            <SourceControlPanel />
          </div>
          <div hidden={layout.view !== 'changes'} className="h-full">
            <ChangesPanel />
          </div>
          <div hidden={layout.view !== 'problems'} className="h-full">
            <ProblemsPanel />
          </div>
          <div hidden={layout.view !== 'chat'} className="h-full">
            <Sessions state={state} />
          </div>
        </aside>
        {layout.sideOpen && <Splitter label="Resize the side bar" orientation="vertical" value={layout.side} min={bounds.side[0]} max={bounds.side[1]} grow={1} onChange={(side) => change({ side })} />}

        <main className="flex min-w-0 flex-1 flex-col">
          <EditorArea />
          {layout.panelOpen && <Splitter label="Resize the terminal" orientation="horizontal" value={Math.min(layout.panel, maxPanel)} min={bounds.panel[0]} max={maxPanel} grow={-1} onChange={(panel) => change({ panel })} />}
          {panelUsed && (
            <div hidden={!layout.panelOpen} style={{ height: Math.min(layout.panel, maxPanel) }} className="shrink-0">
              <Suspense
                fallback={
                  <div className="grid h-full place-items-center">
                    <Spinner label="Loading the terminal" />
                  </div>
                }
              >
                <TerminalPanel shown={layout.panelOpen} onHide={() => change({ panelOpen: false })} focusKey={terminalFocus} />
              </Suspense>
            </div>
          )}
        </main>

        {layout.chatOpen && <Splitter label="Resize the chat" orientation="vertical" value={layout.chat} min={bounds.chat[0]} max={bounds.chat[1]} grow={-1} onChange={(chat) => change({ chat })} />}
        <aside hidden={!layout.chatOpen} aria-label="Agent" style={{ width: layout.chat }} className="min-h-0 shrink-0">
          <Thread
            key={state.session}
            state={state}
            config={config}
            onOpenList={() => show('chat')}
            onHide={() => change({ chatOpen: false })}
            onShow={showChat}
            onEvent={onEvent}
            onTurnEnd={onTurnEnd}
          />
        </aside>
      </div>
      <StatusBar
        state={state}
        onChanges={() => show('changes')}
        onProblems={() => show('problems')}
        onBranch={() => show('git')}
        onTerminal={togglePanel}
        onChat={() => change({ chatOpen: true })}
        onAbout={() => setAbout(true)}
        completing={completing}
        onCompleting={(on) => {
          setCompleting(on)
          void savePreferences({ completion: on }).catch(() => undefined)
        }}
      />
      <About open={about} onOpenChange={setAbout} state={state} />
      {palette && <CommandPalette open={palette} onOpenChange={setPalette} commands={commands()} />}
    </div>
  )
}
