import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Blocks, Brain, Bug, CircleAlert, CircleHelp, Files, GitBranch, GitCompareArrows, Info, ListChecks, MessagesSquare, ScanSearch, Search, SquareTerminal, type LucideIcon } from 'lucide-react'
import { lazy, Suspense, useCallback, useEffect, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { toast } from '@/components/ui/toaster'
import { Alert } from '@/components/ui/alert'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { menuContent, menuItem } from '@/components/ui/dropdown-menu'
import { ContextMenu } from 'radix-ui'
import { ApiError, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { configQuery } from '@/pages/chat/api'
import type { ChatConfig } from '@/pages/chat/types'
import { stateQuery, type CodeEvent, type CodeState } from './api'
import { ChangesPanel } from './changes'
import { Sessions, ThemeMenu, Thread } from './chat'
import { useCommands, type Command } from './commands'
import { chooseAndAddFolder, chooseAndOpenFolder, foldersQuery, openRecentFolder } from './folders'
import { completion } from './completion'
import { CommandPalette, recentLimit, type PaletteCommand } from './palette'
import { MemoryPanel } from './memory-panel'
import { ManageMenu } from './manage-menu'
import { AccountsMenu } from './accounts-menu'
import { PlanPanel } from './plan-panel'
import { ProblemsPanel } from './problems'
import { ReviewPanel } from './review'
import { SourceControlPanel } from './scm'
import { EditorArea, EditorProvider } from './editor'
import { userFile } from './editor-options'
import { CodeRefsProvider } from './refs'
import { rankFiles, useEditor } from './editor-state'
import { Explorer } from './explorer'
import { HelpProvider } from './help'
import { useOpenHelp } from './help-context'
import { openProgram, terminalsQuery, type TerminalInfo } from './ide-api'
import { allFilesQuery, changesQuery, folderQuery, gitBranchesQuery, gitFileLog, gitLogQuery, gitStatusQuery, readFile, saveWorkspaceState, workspaceStateQuery, type DiskChanges, type WorkspaceState, parentOf, preferencesQuery, problemsQuery, savePreferences, writeFile } from './ide-api'
import { useDiskChanges } from './disk-watch'
import { setContext, useKeyboard, useKeyFor } from './keyboard'
import { quickPick } from './quick-pick'
import { QuickPickHost } from './quick-pick-host'
import { DebugConsole } from './debug/console'
import { DebugToolbar } from './debug/toolbar'
import { RunDebugView } from './debug/view'
import { debug, Session, useDebug } from './debug/session'
import { startConfiguration, startTask } from './debug/start'
import { runConfigsQuery, stopTask, tasksQuery } from './debug/run-api'
import { PasteDialog } from './paste-dialog'
import { TrustDialog } from './trust-dialog'
import { askTrust } from './trust'
import { editorsOwn, formatError, formatModel, formattersOf, skipFormat } from './format'
import { clipboardText } from './paste'
import { compareSelection, type Side } from './compare'
import { SearchPanel } from './search'
import { SettingsProvider, useSettings, workspaceSettingsPath } from './settings'
import { allColorThemes, allIconThemes, themeNamed } from './settings-schema'
import { previewTheme, useShownTheme, useThemeSync } from './theme'
import { ensureTheme } from './extensions'
import { ExtensionsView } from './extensions-view'
import { appendElement } from './jsonc'
import { Splitter } from './splitter'
import { bounds, inPanel, layoutOf, panelParts, reorderPanel, reorderSide, resetLocations, sideViews, toPanel, toSide, type Layout, type PanelTab, type View } from './workbench-layout'
import { useNotices } from './sync'
import { About, StatusBar } from './status-bar'
import { languageServers } from './lsp/servers'
import { OutputPanel } from './lsp/output'
import { wholePath } from './whole-path'

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
  // The layout as it was left, in any run: waited for, so the workbench opens as it was.
  const preferences = useQuery(preferencesQuery)

  useEffect(() => {
    if (state.data) document.title = `${state.data.project} · Code Arena`
  }, [state.data])
  // What code-arena said while nothing ran (the sync's trouble, the web's news taken in): shown as it comes.
  useNotices(state.data)

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
    <SettingsProvider>
      <EditorProvider>
        <HelpProvider manual={state.data.manual}>
          <CodeRefsProvider folder={state.data.folder}>
            <SettingsRead>
              <Workbench state={state.data} config={config.data} lost={state.error ?? config.error} saved={preferences.data?.layout} recentSaved={preferences.data?.paletteRecent} />
            </SettingsRead>
            <QuickPickHost />
            <PasteDialog />
            <TrustDialog folder={state.data.project} />
          </CodeRefsProvider>
        </HelpProvider>
      </EditorProvider>
    </SettingsProvider>
  )
}

/** The workbench once the settings are read: it opens in the person's theme, with their keys. */
function SettingsRead({ children }: { children: ReactNode }) {
  return useSettings().ready ? children : null
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

const views: { id: View; label: string; icon: LucideIcon; command: string }[] = [
  { id: 'explorer', label: 'Explorer', icon: Files, command: 'workbench.view.explorer' },
  { id: 'search', label: 'Search', icon: Search, command: 'workbench.action.findInFiles' },
  { id: 'git', label: 'Source control', icon: GitBranch, command: 'workbench.view.scm' },
  { id: 'debug', label: 'Run and Debug', icon: Bug, command: 'workbench.view.debug' },
  { id: 'extensions', label: 'Extensions', icon: Blocks, command: 'workbench.view.extensions' },
  { id: 'changes', label: 'Agent changes', icon: GitCompareArrows, command: 'codeArena.view.changes' },
  { id: 'problems', label: 'Problems', icon: CircleAlert, command: 'workbench.actions.view.problems' },
  { id: 'review', label: 'Review', icon: ScanSearch, command: 'codeArena.view.review' },
  { id: 'plan', label: 'Plan', icon: ListChecks, command: 'codeArena.view.plan' },
  { id: 'memory', label: 'Memory', icon: Brain, command: 'codeArena.view.memory' },
  { id: 'chat', label: 'Chat', icon: MessagesSquare, command: 'codeArena.view.sessions' },
]

function ActivityButton({ label, command, pressed, badge, edge = 'left', onClick, children }: { label: string; command?: string; pressed?: boolean; badge?: number; edge?: 'left' | 'right'; onClick: () => void; children: ReactNode }) {
  const keys = useKeyFor(command ?? '')
  return (
    <Tooltip content={keys ? `${label} (${keys})` : label} side={edge === 'left' ? 'right' : 'left'}>
      <button
        type="button"
        aria-label={badge ? `${label} (${badge})` : label}
        aria-pressed={pressed}
        onClick={onClick}
        className={cn(
          'relative grid size-12 place-items-center text-muted-foreground outline-none transition-colors hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset [&_svg]:size-5',
          pressed && 'text-foreground before:absolute before:inset-y-2 before:w-0.5 before:bg-primary',
          pressed && (edge === 'left' ? 'before:left-0 before:rounded-r' : 'before:right-0 before:rounded-l'),
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

/** What a view or a panel tab carries while dragged: its id (the drop reads it from here; a drag's data is hidden until then). */
const dragged: { current: PanelTab | null } = { current: null }

/** A line where a dragged view or tab would land: before the one it is over. */
const dropLine = (on: boolean, across: boolean) =>
  on ? (across ? 'shadow-[inset_0_2px_0_0_var(--color-primary)]' : 'shadow-[inset_2px_0_0_0_var(--color-primary)]') : ''

/** Starts dragging a view or tab: the move is the only thing a drop does with it. */
function dragging(e: DragEvent<HTMLElement>, id: PanelTab) {
  dragged.current = id
  e.dataTransfer.effectAllowed = 'move'
  e.dataTransfer.setData('text/plain', id)
}

/**
 * A view's button in the activity bar: shows its view, drags to another place in the bar or into the panel (dropped on
 * it, the view goes before it), and has a right-click menu with the same moves and where the side bar sits.
 */
function ViewButton({ view, layout, change, badge, onClick }: { view: (typeof views)[number]; layout: Layout; change: (c: Partial<Layout>) => void; badge?: number; onClick: () => void }) {
  const [over, setOver] = useState(false)
  const side = sideViews(layout)
  const at = side.indexOf(view.id)
  const edge = layout.sideAt
  return (
    <ContextMenu.Root>
      <div
        draggable
        onDragStart={(e) => dragging(e, view.id)}
        onDragEnd={() => (dragged.current = null)}
        onDragOver={(e) => {
          if (dragged.current === null || !(views as { id: string }[]).some((v) => v.id === dragged.current)) return
          e.preventDefault()
          setOver(dragged.current !== view.id)
        }}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => {
          e.preventDefault()
          setOver(false)
          const moved = dragged.current
          dragged.current = null
          if (moved && moved !== view.id && (views as { id: string }[]).some((v) => v.id === moved)) change(reorderSide(layout, moved as View, view.id))
        }}
        className={dropLine(over, true)}
      >
        <ContextMenu.Trigger asChild>
          <div>
            <ActivityButton label={view.label} command={view.command} pressed={layout.sideOpen && layout.view === view.id} badge={badge} edge={edge} onClick={onClick}>
              <view.icon aria-hidden="true" />
            </ActivityButton>
          </div>
        </ContextMenu.Trigger>
      </div>
      <ContextMenu.Portal>
        <ContextMenu.Content className={menuContent} aria-label={`${view.label}: where it goes`}>
          <ContextMenu.Item className={menuItem} onSelect={() => change(toPanel(layout, view.id))}>
            Move to Panel
          </ContextMenu.Item>
          <ContextMenu.Item className={menuItem} disabled={at <= 0} onSelect={() => change(reorderSide(layout, view.id, side[at - 1]))}>
            Move Up
          </ContextMenu.Item>
          <ContextMenu.Item className={menuItem} disabled={at < 0 || at >= side.length - 1} onSelect={() => change(reorderSide(layout, view.id, side[at + 2]))}>
            Move Down
          </ContextMenu.Item>
          <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
          <ContextMenu.Item className={menuItem} onSelect={() => change({ sideAt: edge === 'left' ? 'right' : 'left' })}>
            Move Primary Side Bar {edge === 'left' ? 'Right' : 'Left'}
          </ContextMenu.Item>
          <ContextMenu.Item className={menuItem} onSelect={() => change(resetLocations(layout))}>
            Reset View Locations
          </ContextMenu.Item>
        </ContextMenu.Content>
      </ContextMenu.Portal>
    </ContextMenu.Root>
  )
}

/** The panel's tab names: its own parts' and the views moved there. */
const tabLabel = (t: PanelTab) => (t === 'terminal' ? 'Terminal' : t === 'console' ? 'Debug Console' : t === 'output' ? 'Output' : (views.find((v) => v.id === t)?.label ?? t))

/**
 * A tab of the panel: drags to another place among the tabs or, a view's, back into the activity bar; its right-click
 * menu moves a view back to the side bar and the panel below the editor or beside it.
 */
function PanelTabButton({ tab, layout, change }: { tab: PanelTab; layout: Layout; change: (c: Partial<Layout>) => void }) {
  const [over, setOver] = useState(false)
  const isView = !(panelParts as readonly string[]).includes(tab)
  const across = layout.panelAt === 'bottom'
  return (
    <ContextMenu.Root>
      <ContextMenu.Trigger asChild>
        <button
          type="button"
          role="tab"
          aria-selected={layout.panelTab === tab}
          draggable
          onDragStart={(e) => dragging(e, tab)}
          onDragEnd={() => (dragged.current = null)}
          onDragOver={(e) => {
            if (dragged.current === null) return
            e.preventDefault()
            setOver(dragged.current !== tab)
          }}
          onDragLeave={() => setOver(false)}
          onDrop={(e) => {
            e.preventDefault()
            e.stopPropagation()
            setOver(false)
            const moved = dragged.current
            dragged.current = null
            if (moved && moved !== tab) change(reorderPanel(layout, moved, tab))
          }}
          onClick={() => change({ panelTab: tab })}
          className={cn(
            'shrink-0 border-b pb-1 whitespace-nowrap outline-none focus-visible:ring-1 focus-visible:ring-ring',
            layout.panelTab === tab ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground',
            dropLine(over, !across),
          )}
        >
          {tabLabel(tab)}
        </button>
      </ContextMenu.Trigger>
      <ContextMenu.Portal>
        <ContextMenu.Content className={menuContent} aria-label={`${tabLabel(tab)}: where it goes`}>
          {isView && (
            <>
              <ContextMenu.Item className={menuItem} onSelect={() => change(toSide(layout, tab as View))}>
                Move to Side Bar
              </ContextMenu.Item>
              <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
            </>
          )}
          {(['bottom', 'left', 'right'] as const)
            .filter((at) => at !== layout.panelAt)
            .map((at) => (
              <ContextMenu.Item key={at} className={menuItem} onSelect={() => change({ panelAt: at })}>
                {at === 'bottom' ? 'Move Panel to Bottom' : `Move Panel ${at === 'left' ? 'Left' : 'Right'}`}
              </ContextMenu.Item>
            ))}
          <ContextMenu.Item className={menuItem} onSelect={() => change(resetLocations(layout))}>
            Reset View Locations
          </ContextMenu.Item>
        </ContextMenu.Content>
      </ContextMenu.Portal>
    </ContextMenu.Root>
  )
}

function Workbench({
  state,
  config,
  lost,
  saved,
  recentSaved,
}: {
  state: CodeState
  config: ChatConfig
  lost: unknown
  saved: Record<string, unknown> | undefined
  recentSaved: unknown
}) {
  const queryClient = useQueryClient()
  const ed = useEditor()
  const { refresh, removed, setQuickOpen, openDiff, accept, editorCommands, runAction, showProblems, active } = ed
  const settings = useSettings()
  const [palette, setPalette] = useState(false)
  const [recent, setRecent] = useState<string[]>(() => (Array.isArray(recentSaved) ? recentSaved.filter((x): x is string => typeof x === 'string') : []))
  useThemeSync()
  // The theme shown now (with "window.autoDetectColorScheme", the preferred one of the page's light or dark): the picker starts at it.
  const shownTheme = useShownTheme()
  useKeyboard()
  // Code completion: the person's choice (settings), and not while the agent is working.
  const completing = settings.get<boolean>('codeArena.inlineCompletion.enabled') && settings.get<boolean>('editor.inlineSuggest.enabled')
  useEffect(() => {
    const on = !!state.completion && completing && !state.busy
    completion.wanted = () => on
  }, [state.completion, state.busy, completing])
  const [layout, change] = useLayout(saved)
  // The editor sent lines to the chat: it shows, if hidden.
  const showChat = useCallback(() => change({ chatOpen: true }), [change])
  const [about, setAbout] = useState(false)
  // The folder being opened (Open Folder): code-arena starts again on it, and the page loads it.
  const [opening, setOpening] = useState<string | null>(null)
  const openHelp = useOpenHelp()
  // Bumped to put the focus in the search box or the terminal.
  const [searchFocus, setSearchFocus] = useState(0)
  const [replaceKey, setReplaceKey] = useState(0)
  const [terminalFocus, setTerminalFocus] = useState(0)
  const [newTerminal, setNewTerminal] = useState(0)
  const [terminalShown, setTerminalShown] = useState<{ id: string; at: number } | undefined>()
  /** A terminal a task or a run opened: in the list at once, and shown in the panel. */
  const showTerminal = useCallback(
    (t: TerminalInfo | null | undefined) => {
      if (!t) return
      queryClient.setQueryData<TerminalInfo[]>(terminalsQuery.queryKey, (list) => [...(list ?? []).filter((x) => x.id !== t.id), t])
      setTerminalShown({ id: t.id, at: Date.now() })
    },
    [queryClient],
  )
  const [panelUsed, setPanelUsed] = useState(layout.panelOpen)
  // Run and Debug is drawn the first time it shows (it asks code-arena for the configurations then).
  const [debugUsed, setDebugUsed] = useState(() => layout.view === 'debug' || (layout.panelOpen && layout.panelTab === 'debug'))
  // The Extensions view too (it asks code-arena for VS Code's extensions on this computer then).
  const [extensionsUsed, setExtensionsUsed] = useState(() => layout.view === 'extensions' || (layout.panelOpen && layout.panelTab === 'extensions'))
  // Memory is read from Arena when it is first shown, and again from there.
  const [memoryUsed, setMemoryUsed] = useState(() => layout.view === 'memory' || (layout.panelOpen && layout.panelTab === 'memory'))
  // The Review view reads the last review once it is first shown.
  const [reviewUsed, setReviewUsed] = useState(() => layout.view === 'review' || (layout.panelOpen && layout.panelTab === 'review'))
  const changes = useQuery(changesQuery)
  const [height, setHeight] = useState(() => window.innerHeight)
  const [width, setWidth] = useState(() => window.innerWidth)

  useEffect(() => {
    const on = () => {
      setHeight(window.innerHeight)
      setWidth(window.innerWidth)
    }
    window.addEventListener('resize', on)
    return () => window.removeEventListener('resize', on)
  }, [])

  /** An activity: its view in the side bar; the one shown already closes the side bar, as in an editor. */
  const used = useCallback((view: PanelTab) => {
    if (view === 'debug') setDebugUsed(true)
    if (view === 'extensions') setExtensionsUsed(true)
    if (view === 'memory') setMemoryUsed(true)
    if (view === 'review') setReviewUsed(true)
  }, [])
  /** A view shown where it is: in the side bar, or (moved there) in the panel. */
  const show = useCallback(
    (view: View, toggle = false) => {
      const chat = view === 'chat' ? { chatOpen: true } : {}
      if (inPanel(layout, view)) {
        setPanelUsed(true)
        if (toggle && layout.panelOpen && layout.panelTab === view) change({ panelOpen: false })
        else change({ panelOpen: true, panelTab: view, ...chat })
      } else if (toggle && layout.sideOpen && layout.view === view) change({ sideOpen: false })
      else change({ view, sideOpen: true, ...chat })
      used(view)
      if (view === 'search') setSearchFocus((n) => n + 1)
    },
    [change, layout, used],
  )
  /** A move of the workbench's parts (a view dragged, or put back where it was): what it shows is drawn there. */
  const arrange = useCallback(
    (c: Partial<Layout>) => {
      change(c)
      if (c.view) used(c.view)
      if (c.panelTab) used(c.panelTab)
      if (c.panelOpen) setPanelUsed(true)
    },
    [change, used],
  )

  const togglePanel = useCallback(() => {
    setPanelUsed(true)
    if (!layout.panelOpen && layout.panelTab === 'terminal') setTerminalFocus((n) => n + 1)
    change({ panelOpen: !layout.panelOpen })
  }, [change, layout.panelOpen, layout.panelTab])
  /** Ctrl+`: the terminals, shown (the panel on their tab) or hidden. */
  const toggleTerminal = useCallback(() => {
    setPanelUsed(true)
    if (layout.panelOpen && layout.panelTab === 'terminal') change({ panelOpen: false })
    else {
      setTerminalFocus((n) => n + 1)
      change({ panelOpen: true, panelTab: 'terminal' })
    }
  }, [change, layout.panelOpen, layout.panelTab])

  // What the agent does shows in the editor: a file it edits reloads (when nothing in it is unsaved), a file it makes appears.
  const onEvent = useCallback(
    (e: CodeEvent) => {
      if (e.type !== 'tool_result' || !e.diff) return
      void refresh(e.diff.path)
      // The language servers see it too (a file the editor does not hold, most of all).
      languageServers.filesChanged([{ path: e.diff.path, type: e.diff.created ? 1 : 2 }])
      void queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })
      if (e.diff.created) void queryClient.invalidateQueries({ queryKey: folderQuery(parentOf(e.diff.path)).queryKey })
    },
    [queryClient, refresh],
  )
  // A turn over: its commands may have changed files too.
  const onTurnEnd = useCallback(() => void refresh(), [refresh])

  // Files changed on disk by hand (another editor, a command, git) show as they are: a file open here and not edited here
  // reloads, one deleted closes, the Explorer's folders, Source Control and Changes are read again, and the language
  // servers are told. Null: anything may have changed, so all of it is read again.
  const onDisk = useCallback(
    (d: DiskChanges | null) => {
      const read = (key: readonly unknown[]) => void queryClient.invalidateQueries({ queryKey: key })
      read(gitStatusQuery.queryKey)
      read(changesQuery.queryKey)
      if (!d) {
        void refresh()
        read(['code', 'files'])
        return
      }
      for (const c of d.changes) {
        if (c.path === '') {
          read(gitLogQuery.queryKey)
          read(gitBranchesQuery.queryKey)
          continue
        }
        if (c.kind === 'deleted') removed(c.path)
        else void refresh(c.path)
        if (c.kind !== 'changed') {
          read(folderQuery(parentOf(c.path)).queryKey)
          read(allFilesQuery.queryKey)
        }
      }
      const files = d.changes.filter((c) => c.path !== '')
      if (files.length > 0) languageServers.filesChanged(files.map((c) => ({ path: c.path, type: c.kind === 'created' ? 1 : c.kind === 'deleted' ? 3 : 2 })))
    },
    [queryClient, refresh, removed],
  )
  useDiskChanges(onDisk)

  const count = changes.data?.length ?? 0
  // The check's problems: marked in the editor's files, counted on the activity bar and the status bar.
  const problems = useQuery(problemsQuery).data?.problems
  const errors = problems?.filter((p) => p.severity === 'error').length ?? 0
  const gitCount = useQuery(gitStatusQuery).data?.files?.length ?? 0
  useEffect(() => {
    if (problems) showProblems(problems)
  }, [problems, showProblems])
  const maxPanel = Math.min(bounds.panel[1], Math.max(160, height - 220))

  // What the when clauses read about the workbench.
  setContext({
    sideBarVisible: layout.sideOpen,
    panelVisible: layout.panelOpen,
    auxiliaryBarVisible: layout.chatOpen,
    editorIsOpen: !!active,
    activeEditor: active?.kind === 'file' ? 'workbench.editors.files.textFileEditor' : active ? 'workbench.editors.textDiffEditor' : '',
    textCompareEditorVisible: active?.kind === 'diff',
    activeViewlet: layout.sideOpen ? `workbench.view.${layout.view}` : '',
    resourceLangId: active?.languageId ?? '',
    resourceFilename: active ? active.path.slice(active.path.lastIndexOf('/') + 1) : '',
  })

  const file = active?.kind === 'file' && active.status === 'ready'
  // A tab with a file to save: a file's, or a compare with a file's side.
  const savable = file || (active?.kind === 'diff' && [active.left, active.right].some((x) => x?.kind === 'file'))
  const copy = (text: string) =>
    void navigator.clipboard?.writeText(text).then(
      () => toast.success('Copied.'),
      () => toast.error('The browser did not let the page copy.'),
    )
  /** Color Theme (Ctrl+K Ctrl+Alt+T): Code Arena's and the extensions' themes, each shown as it is chosen in the list (Escape goes back). */
  const pickTheme = async () => {
    const now = themeNamed(shownTheme).name
    const picked = await quickPick({
      title: 'Color theme',
      placeholder: 'Select a color theme (up and down to preview)',
      active: now,
      items: allColorThemes().map((t) => ({ id: t.name, label: t.name, description: t.name === now ? 'current' : t.extension, group: t.kind === 'light' ? 'light themes' : 'dark themes' })),
      onActive: (item) => {
        if (!item) return
        void ensureTheme(item.id)
        previewTheme(item.id)
      },
    })
    try {
      if (!picked) return
      await settings.update('workbench.colorTheme', picked.id)
      if (settings.get('window.autoDetectColorScheme')) await settings.update('window.autoDetectColorScheme', false)
    } catch (e) {
      toast.error(e instanceof Error ? e.message : String(e))
    } finally {
      previewTheme(null)
    }
  }
  /** File Icon Theme: Code Arena's own icons, or an extension's. */
  const pickIconTheme = async () => {
    const now = settings.get<string>('workbench.iconTheme')
    const picked = await quickPick({
      title: 'File icon theme',
      placeholder: 'Select a file icon theme',
      active: now,
      items: allIconThemes().map((t) => ({ id: t.id, label: t.label, description: t.id === now ? 'current' : t.extension })),
    })
    if (picked) await settings.update('workbench.iconTheme', picked.id).catch((e: unknown) => toast.error(errorMessage(e)))
  }
  /** Install from VSIX…: the Extensions view, and the browser's file chooser for a .vsix. */
  const installVsix = () => {
    show('extensions')
    requestAnimationFrame(() => document.querySelector<HTMLInputElement>('input[data-vsix]')?.click())
  }
  const pickLanguage = async () => {
    const list = ed.languages()
    const picked = await quickPick({
      title: 'Language mode',
      placeholder: 'Select a language mode',
      items: [
        ...(active?.languageId ? [{ id: `configure:${active.languageId}`, label: `Configure '${active.language ?? active.languageId}' language based settings…`, group: '' }] : []),
        ...list.map((l) => ({ id: l.id, label: l.name, description: `(${l.id})`, detail: l.id === active?.languageId ? 'current' : undefined, group: 'languages' })),
      ],
    })
    if (!picked) return
    if (picked.id.startsWith('configure:')) {
      const id = picked.id.slice(10)
      try {
        // An empty block for the language in the person's settings, to fill in.
        if (!settings.user.languages[id]) await settings.update(`[${id}]`, {}, 'user')
      } catch (e) {
        toast.error(e instanceof Error ? e.message : String(e))
      }
      void ed.open(userFile('settings.json'))
      return
    }
    ed.setLanguage(picked.id)
  }
  /** The project's settings file, made (an empty object) when it is not there yet. */
  const openWorkspaceSettings = async () => {
    if (settings.workspaceText === null) {
      try {
        await writeFile(workspaceSettingsPath, '{\n}\n', 'none')
        await settings.reload()
      } catch (e) {
        toast.error(errorMessage(e))
        return
      }
    }
    await ed.open(workspaceSettingsPath)
  }
  const compareWithClipboard = async () => {
    const path = active?.path
    if (!path) return
    const text = await clipboardText(`Compare ${path.slice(path.lastIndexOf('/') + 1)} with the clipboard`)
    if (text !== undefined) await ed.compare({ kind: 'clipboard', text }, { kind: 'file', path })
  }
  const compareFileWith = async () => {
    const path = active?.path
    if (!path) return
    let all: string[] = []
    try {
      all = (await queryClient.fetchQuery(allFilesQuery)).files
    } catch (e) {
      toast.error(errorMessage(e))
      return
    }
    const others = all.filter((p) => p !== path)
    const item = (p: string) => ({ id: p, label: p.slice(p.lastIndexOf('/') + 1), description: p })
    // Ranked and cut as Quick Open's: a folder of tens of thousands of files is never drawn whole.
    const picked = await quickPick({
      title: 'Compare with',
      placeholder: `Select a file to compare ${path.slice(path.lastIndexOf('/') + 1)} with`,
      items: [],
      match: (typed) => rankFiles(others, typed).map(item),
    })
    if (picked) await ed.compare({ kind: 'file', path: picked.id }, { kind: 'file', path })
  }
  const saveWithoutFormatting = async () => {
    skipFormat.once = true
    try {
      await ed.save()
    } finally {
      skipFormat.once = false
    }
  }
  /** Format Document With…: the file's formatters, and the default to set for its language. */
  const formatWith = async () => {
    const model = ed.activeModel()
    const path = active?.path
    if (!model || !path) return
    const language = model.getLanguageId()
    const list = await formattersOf(path, language)
    const items = [
      ...(list?.formatters ?? []).map((f) => ({
        id: f.id,
        label: f.name,
        description: [f.version, f.source === 'project' ? 'the project\'s' : f.source === 'config' ? 'config.json' : 'installed'].filter(Boolean).join(' · '),
        detail: !f.available ? (f.reason === 'trust' ? 'needs the folder trusted' : 'not installed') : f.id === list?.default ? 'default' : undefined,
      })),
      { id: editorsOwn, label: "The editor's own", description: 'Monaco (TypeScript, JSON, CSS, HTML)' },
      { id: 'configure', label: 'Configure Default Formatter…', group: ' ' },
    ]
    const picked = await quickPick({ title: 'Format document with', placeholder: 'Select a formatter', items })
    if (!picked) return
    if (picked.id === 'configure') {
      const which = await quickPick({ title: `Default formatter for ${active?.language ?? language}`, placeholder: 'Select the default formatter', items: [...(list?.formatters ?? []).map((f) => ({ id: f.id, label: f.name })), { id: editorsOwn, label: "The editor's own" }] })
      if (which) await settings.update('editor.defaultFormatter', which.id, 'user', language).catch((e: unknown) => toast.error(errorMessage(e)))
      return
    }
    try {
      // The editor's own runs whatever the project's formatters are (Monaco's, not the first provider it finds).
      if ((await formatModel(model, path, { formatter: picked.id })) === null && picked.id === editorsOwn) toast.info(`The editor has no formatter of its own for ${active?.language ?? language}.`)
    } catch (e) {
      toast.error(formatError(e))
    }
  }
  /** A revision (a branch, one of the last commits) against the file as the editor holds it. */
  const compareWithRevision = async () => {
    const path = active?.path
    if (!path) return
    const [branches, log] = await Promise.all([queryClient.fetchQuery(gitBranchesQuery).catch(() => []), gitFileLog(path, 30).catch(() => [])])
    const picked = await quickPick({
      title: 'Compare with revision',
      placeholder: `Select a branch or a commit to compare ${path.slice(path.lastIndexOf('/') + 1)} with`,
      items: [
        { id: 'HEAD', label: 'HEAD', description: 'the last commit', group: '' },
        { id: ':', label: 'Index', description: 'what is staged', group: '' },
        ...branches.map((b) => ({ id: b.name, label: b.name, description: b.current ? 'current branch' : undefined, group: 'branches' })),
        ...log.filter((c) => c.pathAt).map((c) => ({ id: c.hash, label: c.subject, description: `${c.hash.slice(0, 8)} · ${c.author}`, group: 'commits of the file' })),
      ],
    })
    if (picked) await ed.compare({ kind: 'git', path, rev: picked.id }, { kind: 'file', path })
  }
  /** The commits that changed the file: one opens its change to the file (its parent ↔ it). */
  const fileHistory = async () => {
    const path = active?.path
    if (!path) return
    let log
    try {
      log = await gitFileLog(path)
    } catch (e) {
      toast.error(errorMessage(e))
      return
    }
    const picked = await quickPick({
      title: `History of ${path.slice(path.lastIndexOf('/') + 1)}`,
      placeholder: 'Select a commit to see its change to the file',
      items: log.map((c) => ({ id: c.hash, label: c.subject, description: `${c.hash.slice(0, 8)} · ${c.author} · ${new Date(c.at).toLocaleDateString()}`, value: c })),
    })
    const c = picked?.value
    if (!c?.pathAt) return
    const left: Side = c.status === 'A' ? { kind: 'empty', path: c.pathAt } : { kind: 'git', path: c.from ?? c.pathAt, rev: `${c.hash}^` }
    const right: Side = c.status === 'D' ? { kind: 'empty', path: c.pathAt } : { kind: 'git', path: c.pathAt, rev: c.hash }
    await ed.compare(left, right)
  }
  // The debugger: its breakpoints and watches as this folder left them, sessions still running after a reload, and
  // where a session stops shown in the editor.
  const dbg = useDebug()
  const workspace = useQuery(workspaceStateQuery)
  const loaded = useRef(false)
  useEffect(() => {
    if (loaded.current || !workspace.data) return
    loaded.current = true
    debug.root = state.folder
    debug.load(workspace.data)
    void debug.reattach()
  }, [workspace.data, state.folder])
  useEffect(() => {
    debug.inlineValues = () => settings.get('debug.inlineValues') !== 'off'
    // runInTerminal: the program in a terminal of its own, shown in the panel; the adapter is told its process.
    debug.onRunInTerminal = async (s, args) => {
      const [program, ...rest] = args.args
      if (!program) return undefined
      try {
        // Its program's terminal stays in front: the Debug Console does not take the panel when the session has started.
        s.ranInTerminal = true
        const t = await openProgram({ program, args: rest, cwd: args.cwd || undefined, env: args.env, title: args.title, cols: 120, rows: 30 })
        showTerminal(t)
        setPanelUsed(true)
        change({ panelOpen: true, panelTab: 'terminal' })
        return t.pid
      } catch (e) {
        toast.error(errorMessage(e))
        return undefined
      }
    }
    debug.onStop = (_s, frame) => {
      if (frame.path) void ed.open(frame.path, { line: frame.line, column: frame.column })
      // "debug.openDebug": Run and Debug shows as the program stops.
      if (settings.get('debug.openDebug') !== 'neverOpen') show('debug')
    }
    return () => {
      debug.onStop = null
      debug.onRunInTerminal = null
    }
  }, [ed, settings, show, change, showTerminal])
  // The language servers: the folder's trust, the settings and the editor, as they are now.
  const trusted = workspace.data?.trust === 'trusted'
  const { open: openFile, keep, preload } = ed
  useEffect(() => {
    languageServers.configure({ trusted, settings: settings.all, folder: state.folder, project: state.project, open: (path, at) => void openFile(path, at), keep, preload })
  }, [trusted, settings.all, state.folder, state.project, openFile, keep, preload])
  /** The language server of the file shown, or one of those running (chosen when there are several). */
  const pickServer = async (args: unknown): Promise<{ id?: string; language?: string } | null> => {
    const id = (args as { id?: unknown } | undefined)?.id
    if (typeof id === 'string') return { id }
    if (active?.kind === 'file' && active.languageId && languageServers.statusFor(active.languageId)) return { language: active.languageId }
    const running = languageServers.channels()
    if (running.length === 0) {
      toast.info('No language server has started: open a file of a language one serves.')
      return null
    }
    if (running.length === 1) return { id: running[0]!.id }
    const picked = await quickPick({ title: 'Language server', placeholder: 'Select a language server', items: running.map((r) => ({ id: r.id, label: r.name })) })
    return picked ? { id: picked.id } : null
  }
  const showOutput = () => {
    setPanelUsed(true)
    change({ panelOpen: true, panelTab: 'output' })
  }
  useCommands([
    { id: 'codeArena.languageServer.restart', category: 'Language Server', title: 'Restart Language Server', run: async (args) => {
      const which = await pickServer(args)
      if (which) await languageServers.restart(which)
    } },
    { id: 'codeArena.languageServer.showLog', category: 'Language Server', title: 'Show Language Server Log', run: async (args) => {
      const which = await pickServer(args)
      const id = which?.id ?? (which?.language ? languageServers.statusFor(which.language)?.id : undefined)
      if (id) languageServers.show(id)
      showOutput()
    } },
    { id: 'workbench.action.output.toggleOutput', category: 'View', title: 'Toggle Output', run: () => {
      setPanelUsed(true)
      if (layout.panelOpen && layout.panelTab === 'output') change({ panelOpen: false })
      else change({ panelOpen: true, panelTab: 'output' })
    } },
  ])
  const s = dbg.active
  setContext({ inDebugMode: dbg.sessions.some((x) => x.state !== 'ended'), debugState: s?.state ?? 'inactive', debugType: s?.start.adapterId ?? '' })
  const editorContext = () => {
    const model = ed.activeModel()
    const piece = ed.selection(false)
    const text = model && piece ? model.getValueInRange({ startLineNumber: piece.startLine, startColumn: 1, endLineNumber: piece.endLine, endColumn: model.getLineMaxColumn(piece.endLine) }) : undefined
    return { file: active?.kind === 'file' && active.path && !active.path.startsWith('user:') ? active.path : undefined, lineNumber: ed.cursor?.line, selectedText: text, settings: settings.all() }
  }
  const startOne = async (name: string, noDebug: boolean, group: string | null) => {
    const done = await startConfiguration(name, noDebug, editorContext(), queryClient, group)
    if (!done) return
    setPanelUsed(true)
    if (!(done instanceof Session)) {
      showTerminal(done.terminal)
      change({ panelOpen: true, panelTab: 'terminal' })
      return
    }
    if (settings.get('debug.openDebug') === 'openOnSessionStart') show('debug')
    // launch.json's own word first; a program in a terminal of its own (runInTerminal, "console": "integratedTerminal")
    // keeps that terminal in front.
    const own = done.start.configuration.console
    if (done.ranInTerminal || own === 'integratedTerminal' || own === 'externalTerminal') return
    const open = done.start.internalConsoleOptions ?? settings.get('debug.internalConsoleOptions')
    if (open === 'openOnSessionStart' || (open === 'openOnFirstSessionStart' && dbg.sessions.length <= 1)) change({ panelOpen: true, panelTab: 'console' })
  }
  const compounds = useRef(0)
  /** Starts a configuration with its debugger, or without; a compound starts each of its own. */
  const start = async (name: string, noDebug: boolean) => {
    // Saving what is unsaved first ("debug.saveBeforeStart").
    if (settings.get('debug.saveBeforeStart') !== 'none') await ed.saveAll()
    const configs = await queryClient.fetchQuery(runConfigsQuery).catch(() => null)
    const compound = configs && !configs.configurations.some((c) => c.name === name) ? configs.compounds.find((c) => c.name === name) : undefined
    if (!compound) {
      await startOne(name, noDebug, null)
      return
    }
    // As VS Code's: its preLaunchTask once, done (or ready) first; then its configurations together.
    if (compound.preLaunchTask && !(await startTask({ label: compound.preLaunchTask, wait: true }, editorContext()))) return
    const group = compound.stopAll ? `${compound.name}:${++compounds.current}` : null
    await Promise.all(compound.configurations.map((c) => startOne(c, noDebug, group)))
  }
  /** F5: the configuration (or compound) chosen in Run and Debug (the first when none was), or the view to choose one. */
  const startChosen = async (noDebug: boolean) => {
    const configs = await queryClient.fetchQuery(runConfigsQuery).catch(() => null)
    const shown = configs?.configurations.filter((c) => !c.hidden) ?? []
    const saved = queryClient.getQueryData<WorkspaceState>(workspaceStateQuery.queryKey)?.['launch.selected'] ?? workspace.data?.['launch.selected']
    const chosen = shown.find((c) => c.name === saved)?.name ?? configs?.compounds.find((c) => c.name === saved)?.name ?? shown[0]?.name
    if (!chosen) {
      setDebugUsed(true)
      show('debug')
      return
    }
    await start(chosen, noDebug)
  }
  const pickTask = async (title: string, running = false) => {
    const list = await queryClient.fetchQuery(tasksQuery).catch((e: unknown) => {
      toast.error(errorMessage(e))
      return null
    })
    if (!list) return undefined
    const tasks = list.tasks.filter((t) => !running || t.running)
    if (tasks.length === 0) {
      toast.info(running ? 'No task is running.' : 'No task: add one to .vscode/tasks.json.')
      return undefined
    }
    return (await quickPick({ title, placeholder: 'Select the task', items: tasks.map((t) => ({ id: t.label, label: t.label, description: t.command ?? (t.dependsOn?.join(', ') || undefined), group: t.source === 'workspace' ? 'tasks.json' : 'detected', value: t })) }))?.value
  }
  const runTaskBy = async (which: { label?: string; group?: 'build' | 'test' }) => {
    const tasks = (await queryClient.fetchQuery(tasksQuery).catch(() => null))?.tasks
    const done = await startTask(which, editorContext(), tasks)
    for (const t of done?.terminals ?? []) showTerminal(t)
    if (done?.terminal) showTerminal(done.terminal)
    if (done) {
      setPanelUsed(true)
      change({ panelOpen: true, panelTab: 'terminal' })
    }
  }
  /** .vscode/launch.json, made with VS Code's skeleton when it is not there. */
  const openLaunchJson = async () => {
    try {
      await readFile('.vscode/launch.json')
    } catch {
      await writeFile('.vscode/launch.json', '{\n    "version": "0.2.0",\n    "configurations": []\n}\n', 'none').catch((e: unknown) => toast.error(errorMessage(e)))
    }
    await ed.open('.vscode/launch.json')
  }
  /** Add Configuration…: one the project's files give, or a type's template, at the end of launch.json's configurations. */
  const addConfiguration = async () => {
    const configs = await queryClient.fetchQuery(runConfigsQuery).catch(() => null)
    const detected = (configs?.configurations ?? []).filter((c) => c.source === 'detected' && c.configuration)
    const templates: Record<string, Record<string, unknown>> = {
      '.NET: Launch': { name: '.NET: Launch', type: 'coreclr', request: 'launch', preLaunchTask: 'build', program: '${workspaceFolder}/bin/Debug/net10.0/app.dll', args: [], cwd: '${workspaceFolder}', stopAtEntry: false },
      'Node.js: Launch Program': { name: 'Launch Program', type: 'node', request: 'launch', program: '${workspaceFolder}/index.js' },
      'Python: Current File': { name: 'Python: Current File', type: 'debugpy', request: 'launch', program: '${file}', console: 'integratedTerminal' },
      'Go: Launch Package': { name: 'Launch Package', type: 'go', request: 'launch', mode: 'auto', program: '${workspaceFolder}' },
      'C/C++: gdb Launch': { name: '(gdb) Launch', type: 'cppdbg', request: 'launch', program: '${workspaceFolder}/a.out', args: [], cwd: '${workspaceFolder}', MIMode: 'gdb' },
      'C/C++: gdb Attach': { name: '(gdb) Attach', type: 'cppdbg', request: 'attach', program: '${workspaceFolder}/a.out', processId: '${command:pickProcess}', MIMode: 'gdb' },
      '.NET: Attach': { name: '.NET: Attach', type: 'coreclr', request: 'attach', processId: '${command:pickProcess}' },
    }
    const picked = await quickPick({
      title: 'Add configuration',
      placeholder: 'Select a configuration to add to launch.json',
      items: [
        ...detected.map((c) => ({ id: `d:${c.name}`, label: c.name, description: c.command ?? undefined, group: 'detected in the project', value: c.configuration! })),
        ...Object.entries(templates).map(([label, c]) => ({ id: `t:${label}`, label, group: 'templates', value: c })),
      ],
    })
    if (!picked?.value) return
    let file: { text: string | null; version: string } | null = null
    try {
      file = await readFile('.vscode/launch.json')
    } catch {
      file = null
    }
    const text = file?.text?.trim() ? file.text : '{\n    "version": "0.2.0",\n    "configurations": []\n}\n'
    try {
      await writeFile('.vscode/launch.json', appendElement(text, ['configurations'], picked.value), file?.version ?? 'none')
      void queryClient.invalidateQueries({ queryKey: runConfigsQuery.queryKey })
      await ed.open('.vscode/launch.json')
      await ed.refresh('.vscode/launch.json')
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  const debugCommands: Command[] = [
    { id: 'workbench.view.debug', category: 'View', title: 'Show Run and Debug', run: () => {
      setDebugUsed(true)
      show('debug')
    } },
    { id: 'workbench.view.extensions', category: 'View', title: 'Show Extensions', run: () => show('extensions') },
    { id: 'workbench.extensions.action.installVSIX', category: 'Extensions', title: 'Install from VSIX…', run: installVsix },
    { id: 'workbench.action.debug.start', category: 'Debug', title: 'Start Debugging', run: () => void startChosen(false) },
    { id: 'workbench.action.debug.run', category: 'Debug', title: 'Run Without Debugging', run: () => void startChosen(true) },
    { id: 'workbench.action.debug.continue', category: 'Debug', title: 'Continue', enabled: () => s?.state === 'stopped', run: () => void s?.continue() },
    { id: 'workbench.action.debug.pause', category: 'Debug', title: 'Pause', enabled: () => s?.state === 'running', run: () => void s?.pause() },
    { id: 'workbench.action.debug.stepOver', category: 'Debug', title: 'Step Over', enabled: () => s?.state === 'stopped', run: () => void s?.next() },
    { id: 'workbench.action.debug.stepInto', category: 'Debug', title: 'Step Into', enabled: () => s?.state === 'stopped', run: () => void s?.stepIn() },
    { id: 'workbench.action.debug.stepOut', category: 'Debug', title: 'Step Out', enabled: () => s?.state === 'stopped', run: () => void s?.stepOut() },
    { id: 'workbench.action.debug.stop', category: 'Debug', title: 'Stop', enabled: () => !!s, run: () => void debug.stop() },
    { id: 'workbench.action.debug.restart', category: 'Debug', title: 'Restart', enabled: () => !!s, run: async () => {
      const name = s?.name
      await debug.stop()
      if (name) await start(name, false)
    } },
    { id: 'editor.debug.action.toggleBreakpoint', category: 'Debug', title: 'Toggle Breakpoint', enabled: () => file, run: () => active && ed.cursor && debug.toggle(active.path, ed.cursor.line) },
    { id: 'workbench.debug.viewlet.action.removeAllBreakpoints', category: 'Debug', title: 'Remove All Breakpoints', run: () => debug.removeAll() },
    { id: 'workbench.debug.action.toggleRepl', category: 'View', title: 'Toggle Debug Console', run: () => {
      setPanelUsed(true)
      if (layout.panelOpen && layout.panelTab === 'console') change({ panelOpen: false })
      else change({ panelOpen: true, panelTab: 'console' })
    } },
    { id: 'workbench.action.debug.configure', category: 'Debug', title: 'Open launch.json', run: () => void openLaunchJson() },
    { id: 'debug.addConfiguration', category: 'Debug', title: 'Add Configuration…', run: () => void addConfiguration() },
    { id: 'workbench.action.debug.selectandstart', category: 'Debug', title: 'Select and Start Debugging', run: async () => {
      const configs = await queryClient.fetchQuery(runConfigsQuery).catch(() => null)
      const picked = await quickPick({
        title: 'Start debugging',
        placeholder: 'Select a configuration',
        items: [
          ...(configs?.configurations ?? []).filter((c) => !c.hidden).map((c) => ({ id: c.name, label: c.name, description: c.command ?? undefined, group: c.source === 'launch' ? 'launch.json' : 'detected' })),
          ...(configs?.compounds ?? []).map((c) => ({ id: c.name, label: c.name, description: c.configurations.join(', '), group: 'compounds' })),
        ],
      })
      if (picked) {
        void saveWorkspaceState({ 'launch.selected': picked.id }).then(() => queryClient.invalidateQueries({ queryKey: workspaceStateQuery.queryKey }))
        await start(picked.id, false)
      }
    } },
    { id: 'workbench.action.tasks.runTask', category: 'Tasks', title: 'Run Task', run: async () => {
      const t = await pickTask('Run task')
      if (t) await runTaskBy({ label: t.label })
    } },
    { id: 'workbench.action.tasks.build', category: 'Tasks', title: 'Run Build Task', run: () => void runTaskBy({ group: 'build' }) },
    { id: 'workbench.action.tasks.test', category: 'Tasks', title: 'Run Test Task', run: () => void runTaskBy({ group: 'test' }) },
    { id: 'workbench.action.tasks.terminate', category: 'Tasks', title: 'Terminate Task', run: async () => {
      const t = await pickTask('Terminate task', true)
      if (t?.terminal) await stopTask(t.terminal.id).catch((e: unknown) => toast.error(errorMessage(e)))
    } },
    { id: 'codeArena.selectBuildConfiguration', category: 'Code Arena', title: 'Select the Build Configuration (Debug or Release)', run: async () => {
      const picked = await quickPick({ title: 'Build configuration', placeholder: 'Debug or Release', items: [{ id: 'Debug', label: 'Debug' }, { id: 'Release', label: 'Release' }] })
      if (picked) {
        await saveWorkspaceState({ buildConfiguration: picked.id }).catch((e: unknown) => toast.error(errorMessage(e)))
        void queryClient.invalidateQueries({ queryKey: ['code', 'run'] })
        void queryClient.invalidateQueries({ queryKey: tasksQuery.queryKey })
      }
    } },
  ]
  useCommands(debugCommands)
  const workbenchCommands: Command[] = [
    { id: 'workbench.action.showCommands', category: 'View', title: 'Show All Commands', hidden: true, run: () => setPalette(true) },
    { id: 'workbench.action.quickOpen', category: 'File', title: 'Go to File…', run: () => setQuickOpen(true) },
    { id: 'workbench.action.gotoLine', category: 'Go', title: 'Go to Line/Column…', enabled: () => file, run: () => setQuickOpen(true, ':') },
    { id: 'workbench.action.gotoSymbol', category: 'Go', title: 'Go to Symbol in Editor…', enabled: () => file, run: () => runAction('editor.action.quickOutline') },
    { id: 'workbench.action.showAllSymbols', category: 'Go', title: 'Go to Symbol in Workspace…', run: () => setQuickOpen(true, '#') },
    { id: 'breadcrumbs.toggle', category: 'View', title: 'Toggle Breadcrumbs', run: () => void settings.update('breadcrumbs.enabled', settings.get('breadcrumbs.enabled') === false) },
    { id: 'breadcrumbs.focusAndSelect', category: 'View', title: 'Focus and Select Breadcrumbs', enabled: () => file, run: () => document.querySelector<HTMLElement>('[data-breadcrumbs] button')?.focus() },
    { id: 'markdown.showPreview', category: 'Markdown', title: 'Open Preview', enabled: () => active?.languageId === 'markdown', run: () => active && void ed.openPreview(active.path) },
    { id: 'markdown.showPreviewToSide', category: 'Markdown', title: 'Open Preview to the Side', enabled: () => active?.languageId === 'markdown', run: () => active && void ed.openPreview(active.path) },
    { id: 'markdown.showSource', category: 'Markdown', title: 'Show Source', enabled: () => active?.kind === 'preview', run: () => active && void ed.open(active.path) },
    { id: 'workbench.action.files.openFolder', category: 'File', title: 'Open Folder…', run: () => void chooseAndOpenFolder(setOpening) },
    { id: 'workbench.action.openRecent', category: 'File', title: 'Open Recent…', run: () => void openRecentFolder(setOpening) },
    { id: 'workbench.action.addRootFolder', category: 'Workspaces', title: 'Add Folder to Workspace…', run: async () => {
      if (await chooseAndAddFolder()) await queryClient.invalidateQueries({ queryKey: foldersQuery.queryKey })
    } },
    { id: 'workbench.action.files.save', category: 'File', title: 'Save', enabled: () => savable, run: () => void ed.save() },
    { id: 'workbench.action.files.saveAll', category: 'File', title: 'Save All', run: () => void ed.saveAll() },
    { id: 'workbench.action.closeActiveEditor', category: 'View', title: 'Close Editor', enabled: () => !!active, run: () => active && void ed.close(active.id) },
    { id: 'workbench.action.closeAllEditors', category: 'View', title: 'Close All Editors', run: () => void ed.closeMany(ed.tabs.map((t) => t.id)) },
    { id: 'workbench.action.closeUnmodifiedEditors', category: 'View', title: 'Close Saved Editors', run: () => void ed.closeMany(ed.tabs.filter((t) => !t.dirty && !t.pinned).map((t) => t.id)) },
    { id: 'workbench.action.closeOtherEditors', category: 'View', title: 'Close Other Editors', run: () => void ed.closeMany(ed.tabs.filter((t) => t.id !== active?.id && !t.pinned).map((t) => t.id)) },
    { id: 'workbench.action.keepEditor', category: 'View', title: 'Keep Editor', enabled: () => !!active?.preview, run: () => ed.keepTab() },
    { id: 'workbench.action.pinEditor', category: 'View', title: 'Pin Editor', enabled: () => !!active && !active.pinned, run: () => active && ed.pin(active.id, true) },
    { id: 'workbench.action.unpinEditor', category: 'View', title: 'Unpin Editor', enabled: () => !!active?.pinned, run: () => active && ed.pin(active.id, false) },
    { id: 'workbench.action.moveEditorLeftInGroup', category: 'View', title: 'Move Editor Left', enabled: () => !!active, run: () => active && ed.moveTab(active.id, ed.tabs.findIndex((t) => t.id === active.id) - 1) },
    { id: 'workbench.action.moveEditorRightInGroup', category: 'View', title: 'Move Editor Right', enabled: () => !!active, run: () => active && ed.moveTab(active.id, ed.tabs.findIndex((t) => t.id === active.id) + 1) },
    { id: 'workbench.action.reopenClosedEditor', category: 'View', title: 'Reopen Closed Editor', run: () => void ed.reopenClosed() },
    { id: 'workbench.action.nextEditor', category: 'View', title: 'Open Next Editor', run: () => ed.cycle(1) },
    { id: 'workbench.action.previousEditor', category: 'View', title: 'Open Previous Editor', run: () => ed.cycle(-1) },
    ...Array.from({ length: 9 }, (_, i): Command => ({ id: `workbench.action.openEditorAtIndex${i + 1}`, category: 'View', title: i === 8 ? 'Open Last Editor in Group' : `Open Editor at Index ${i + 1}`, hidden: true, run: () => ed.activateIndex(i === 8 ? -1 : i) })),
    { id: 'workbench.action.navigateBack', category: 'Go', title: 'Go Back', run: () => ed.navigate(-1) },
    { id: 'workbench.action.navigateForward', category: 'Go', title: 'Go Forward', run: () => ed.navigate(1) },
    { id: 'workbench.action.files.copyPathOfActiveFile', category: 'File', title: 'Copy Path of Active File', enabled: () => !!active, run: () => active && copy(wholePath(state.folder, active.path)) },
    { id: 'copyRelativeFilePath', category: 'File', title: 'Copy Relative Path of Active File', enabled: () => !!active, run: () => active && copy(active.path) },
    { id: 'workbench.action.editor.changeLanguageMode', category: 'Change Language Mode', title: 'Change Language Mode', enabled: () => file, run: () => void pickLanguage() },
    { id: 'editor.action.toggleWordWrap', category: 'View', title: 'Toggle Word Wrap', enabled: () => file, run: () => ed.toggleWordWrap() },
    { id: 'workbench.action.focusFirstEditorGroup', category: 'View', title: 'Focus First Editor Group', run: () => ed.focusEditor() },
    { id: 'workbench.action.focusSideBar', category: 'View', title: 'Focus into Primary Side Bar', run: () => {
      change({ sideOpen: true })
      // The view shown: the others are in the side bar too, hidden.
      requestAnimationFrame(() => document.querySelector<HTMLElement>(['[tabindex="0"]', 'button', 'input'].map((x) => `aside[data-side] > div:not([hidden]) ${x}`).join(', '))?.focus())
    } },
    { id: 'workbench.action.toggleSidebarVisibility', category: 'View', title: 'Toggle Primary Side Bar Visibility', run: () => change({ sideOpen: !layout.sideOpen }) },
    { id: 'workbench.action.toggleSidebarPosition', category: 'View', title: 'Toggle Primary Side Bar Position', run: () => change({ sideAt: layout.sideAt === 'left' ? 'right' : 'left' }) },
    { id: 'workbench.action.positionPanelBottom', category: 'View', title: 'Move Panel to Bottom', enabled: () => layout.panelAt !== 'bottom', run: () => arrange({ panelAt: 'bottom', panelOpen: true }) },
    { id: 'workbench.action.positionPanelLeft', category: 'View', title: 'Move Panel Left', enabled: () => layout.panelAt !== 'left', run: () => arrange({ panelAt: 'left', panelOpen: true }) },
    { id: 'workbench.action.positionPanelRight', category: 'View', title: 'Move Panel Right', enabled: () => layout.panelAt !== 'right', run: () => arrange({ panelAt: 'right', panelOpen: true }) },
    { id: 'codeArena.view.moveToPanel', category: 'View', title: 'Move View to Panel', enabled: () => layout.sideOpen && sideViews(layout).length > 0, run: () => arrange(toPanel(layout, layout.view)) },
    { id: 'codeArena.view.moveToSideBar', category: 'View', title: 'Move Panel View to Side Bar', enabled: () => !(panelParts as readonly string[]).includes(layout.panelTab), run: () => arrange(toSide(layout, layout.panelTab as View)) },
    { id: 'workbench.action.resetViewLocations', category: 'View', title: 'Reset View Locations', run: () => arrange(resetLocations(layout)) },
    { id: 'workbench.action.togglePanel', category: 'View', title: 'Toggle Panel Visibility', run: togglePanel },
    { id: 'workbench.action.toggleAuxiliaryBar', category: 'View', title: 'Toggle Secondary Side Bar Visibility (the chat)', run: () => change({ chatOpen: !layout.chatOpen }) },
    { id: 'workbench.action.chat.open', category: 'Chat', title: 'Open Chat', run: () => {
      change({ chatOpen: true })
      requestAnimationFrame(() => document.querySelector<HTMLElement>('aside[aria-label="Agent"] textarea')?.focus())
    } },
    { id: 'workbench.action.findInFiles', category: 'Search', title: 'Find in Files', run: () => show('search') },
    { id: 'workbench.action.replaceInFiles', category: 'Search', title: 'Replace in Files', run: () => {
      show('search')
      setReplaceKey((n) => n + 1)
    } },
    { id: 'workbench.view.explorer', category: 'View', title: 'Show Explorer', run: () => show('explorer') },
    { id: 'workbench.view.scm', category: 'View', title: 'Show Source Control', run: () => show('git') },
    { id: 'workbench.actions.view.problems', category: 'View', title: 'Show Problems', run: () => show('problems') },
    { id: 'codeArena.view.changes', category: 'View', title: "Show the Agent's Changes", run: () => show('changes') },
    { id: 'codeArena.view.review', category: 'View', title: 'Show Review', run: () => show('review') },
    { id: 'codeArena.view.memory', category: 'View', title: 'Show Memory', run: () => {
      setMemoryUsed(true)
      show('memory')
    } },
    { id: 'codeArena.view.plan', category: 'View', title: "Show the Agent's Plan", run: () => show('plan') },
    { id: 'codeArena.view.sessions', category: 'View', title: 'Show the Chat Sessions', run: () => show('chat') },
    { id: 'codeArena.acceptAllChanges', category: 'Code Arena', title: "Accept All the Agent's Changes", enabled: () => count > 0, run: () => void accept() },
    { id: 'workbench.action.terminal.toggleTerminal', category: 'View', title: 'Toggle Terminal', run: toggleTerminal },
    { id: 'workbench.action.terminal.new', category: 'Terminal', title: 'Create New Terminal', run: () => {
      setPanelUsed(true)
      change({ panelOpen: true, panelTab: 'terminal' })
      setNewTerminal((n) => n + 1)
    } },
    { id: 'workbench.action.openSettings', category: 'Preferences', title: 'Open Settings', run: (args) => ed.openPage('settings', typeof args === 'string' ? args : '') },
    { id: 'workbench.action.openSettingsJson', category: 'Preferences', title: 'Open User Settings (JSON)', run: () => void ed.open(userFile('settings.json')) },
    { id: 'workbench.action.openWorkspaceSettingsFile', category: 'Preferences', title: 'Open Workspace Settings (JSON)', run: () => void openWorkspaceSettings() },
    { id: 'workbench.action.openGlobalKeybindings', category: 'Preferences', title: 'Open Keyboard Shortcuts', run: (args) => ed.openPage('keybindings', typeof args === 'string' ? args : '') },
    { id: 'workbench.action.openGlobalKeybindingsFile', category: 'Preferences', title: 'Open Keyboard Shortcuts (JSON)', run: () => void ed.open(userFile('keybindings.json')) },
    { id: 'workbench.action.selectTheme', category: 'Preferences', title: 'Color Theme', run: () => void pickTheme() },
    { id: 'workbench.action.selectIconTheme', category: 'Preferences', title: 'File Icon Theme', run: () => void pickIconTheme() },
    { id: 'workbench.files.action.compareWithSaved', category: 'File', title: 'Compare Active File with Saved', enabled: () => file, run: () => active && void ed.compare({ kind: 'disk', path: active.path }, { kind: 'file', path: active.path }) },
    { id: 'workbench.files.action.compareWithClipboard', category: 'File', title: 'Compare Active File with Clipboard', enabled: () => file, run: () => void compareWithClipboard() },
    { id: 'workbench.files.action.compareFileWith', category: 'File', title: 'Compare Active File With…', enabled: () => file, run: () => void compareFileWith() },
    { id: 'selectForCompare', category: 'File', title: 'Select for Compare', enabled: () => file, run: () => active && (compareSelection.path = active.path) },
    { id: 'compareFiles', category: 'File', title: 'Compare with Selected', enabled: () => file && !!compareSelection.path && compareSelection.path !== active?.path, run: () => active && compareSelection.path && void ed.compare({ kind: 'file', path: compareSelection.path }, { kind: 'file', path: active.path }) },
    { id: 'workbench.action.compareEditor.nextChange', category: 'Compare', title: 'Go to Next Change', enabled: () => active?.kind === 'diff', run: () => ed.diffEditor()?.goToDiff('next') },
    { id: 'workbench.action.compareEditor.previousChange', category: 'Compare', title: 'Go to Previous Change', enabled: () => active?.kind === 'diff', run: () => ed.diffEditor()?.goToDiff('previous') },
    // Quick diff's changes in the code editor (VS Code's keys for them: Alt+F5, Shift+Alt+F5).
    { id: 'workbench.action.editor.nextChange', category: 'Go', title: 'Go to Next Change in the File', enabled: () => file, run: () => runAction('editor.action.dirtydiff.next') },
    { id: 'workbench.action.editor.previousChange', category: 'Go', title: 'Go to Previous Change in the File', enabled: () => file, run: () => runAction('editor.action.dirtydiff.previous') },
    { id: 'git.stageSelectedRanges', category: 'Git', title: 'Stage Selected Ranges', enabled: () => active?.source === 'git', run: () => void ed.changeAction('stage') },
    { id: 'git.unstageSelectedRanges', category: 'Git', title: 'Unstage Selected Ranges', enabled: () => active?.source === 'staged', run: () => void ed.changeAction('unstage') },
    { id: 'git.revertSelectedRanges', category: 'Git', title: 'Revert Selected Ranges', enabled: () => (active?.source === 'git' || active?.source === 'agent') && active.right?.kind === 'file', run: () => void ed.changeAction('revert') },
    { id: 'codeArena.changes.acceptSelectedRanges', category: 'Code Arena', title: "Accept the Agent's Selected Changes", enabled: () => active?.source === 'agent', run: () => void ed.changeAction('accept') },
    { id: 'git.openChange', category: 'Git', title: 'Open Changes', enabled: () => file, run: () => active && void ed.openDiff(active.path, 'git') },
    { id: 'codeArena.git.compareWithRevision', category: 'Git', title: 'Compare with Revision…', enabled: () => file, run: () => void compareWithRevision() },
    { id: 'codeArena.git.fileHistory', category: 'Git', title: 'Open File History…', enabled: () => file, run: () => void fileHistory() },
    { id: 'codeArena.compare.swapSides', category: 'Compare', title: 'Swap Left and Right Side', enabled: () => active?.kind === 'diff', run: () => active && ed.swapSides(active.id) },
    { id: 'toggle.diff.renderSideBySide', category: 'Compare', title: 'Toggle Inline View', run: () => void settings.update('diffEditor.renderSideBySide', !settings.get('diffEditor.renderSideBySide')) },
    { id: 'toggle.diff.ignoreTrimWhitespace', category: 'Compare', title: 'Toggle Ignore Trim Whitespace', run: () => void settings.update('diffEditor.ignoreTrimWhitespace', !settings.get('diffEditor.ignoreTrimWhitespace')) },
    { id: 'diffEditor.toggleCollapseUnchangedRegions', category: 'Compare', title: 'Toggle Collapse Unchanged Regions', run: () => void settings.update('diffEditor.hideUnchangedRegions.enabled', !settings.get('diffEditor.hideUnchangedRegions.enabled')) },
    { id: 'editor.action.formatDocument.multiple', category: 'Format', title: 'Format Document With…', enabled: () => file, run: () => void formatWith() },
    { id: 'workbench.action.files.saveWithoutFormatting', category: 'File', title: 'Save without Formatting', enabled: () => savable, run: () => void saveWithoutFormatting() },
    { id: 'workbench.trust.manage', category: 'Workspaces', title: 'Manage Workspace Trust', run: () => void askTrust('Choose whether the programs this folder holds may run here.') },
    { id: 'codeArena.help', category: 'Help', title: 'Show Help', run: () => openHelp?.() },
    { id: 'codeArena.about', category: 'Help', title: 'About Code Arena', run: () => setAbout(true) },
  ]
  useCommands(workbenchCommands)


  // The editor's own commands for the palette, while a file is shown: Code Arena's first.
  const editorPalette = (): PaletteCommand[] =>
    editorCommands()
      .map((c): PaletteCommand => ({ id: c.id, label: c.label.startsWith('Code Arena') ? c.label : `Editor: ${c.label}`, run: () => runAction(c.id) }))
      .sort((a, b) => Number(!a.label.startsWith('Code Arena')) - Number(!b.label.startsWith('Code Arena')) || a.label.localeCompare(b.label))
  const ran = (id: string) => {
    const next = [id, ...recent.filter((x) => x !== id)].slice(0, recentLimit)
    setRecent(next)
    void savePreferences({ paletteRecent: next }).catch(() => undefined)
  }
  const side = sideViews(layout)
  const left = layout.sideAt === 'left'
  // Beside the editor, the panel is as wide as the window leaves; below it, as tall.
  const across = layout.panelAt !== 'bottom'
  const maxSidePanel = Math.max(200, width - (layout.sideOpen ? layout.side : 0) - (layout.chatOpen ? layout.chat : 0) - 360)
  const panelSize = Math.min(layout.panel, across ? maxSidePanel : maxPanel)
  /** A view's own part, wherever it is (the side bar or the panel); `shown`: it is on screen now. */
  const viewPart = (v: View, shown: boolean): ReactNode => {
    switch (v) {
      case 'explorer':
        return <Explorer project={state.project} onOpenChanges={(path) => void openDiff(path)} />
      case 'search':
        return <SearchPanel focusKey={searchFocus} replaceKey={replaceKey} />
      case 'git':
        return <SourceControlPanel />
      case 'debug':
        return debugUsed && <RunDebugView onStart={(name, noDebug) => void start(name, noDebug)} />
      case 'extensions':
        return extensionsUsed && <ExtensionsView />
      case 'changes':
        return <ChangesPanel />
      case 'problems':
        return <ProblemsPanel />
      case 'review':
        return reviewUsed && <ReviewPanel />
      case 'plan':
        return shown && <PlanPanel arena={state.manual ? new URL(state.manual).origin : null} />
      case 'memory':
        return memoryUsed && <MemoryPanel />
      case 'chat':
        return <Sessions state={state} />
    }
  }

  const activityBar = (
    <nav key="activity" aria-label="Activity bar" className={cn('flex w-12 shrink-0 flex-col bg-sidebar', left ? 'border-r' : 'border-l')}>
      {/* Dropped below the views: the view goes last (one from the panel comes back to the side bar). */}
      <div
        className="flex min-h-0 flex-1 flex-col"
        onDragOver={(e) => {
          if (dragged.current !== null && views.some((v) => v.id === dragged.current)) e.preventDefault()
        }}
        onDrop={(e) => {
          const moved = dragged.current
          dragged.current = null
          if (moved && views.some((v) => v.id === moved)) {
            e.preventDefault()
            arrange(reorderSide(layout, moved as View))
          }
        }}
      >
        {side.map((id) => {
          const v = views.find((x) => x.id === id)!
          return <ViewButton key={id} view={v} layout={layout} change={arrange} badge={id === 'changes' ? count : id === 'problems' ? errors : id === 'git' ? gitCount : undefined} onClick={() => show(id, true)} />
        })}
        <span className="flex-1" />
      </div>
      <ActivityButton label="Terminal" command="workbench.action.terminal.toggleTerminal" pressed={layout.panelOpen} edge={layout.sideAt} onClick={toggleTerminal}>
        <SquareTerminal aria-hidden="true" />
      </ActivityButton>
      <div className="grid place-items-center pb-1">
        <ThemeMenu side={left ? 'right' : 'left'} align="end" />
      </div>
      <AccountsMenu edge={layout.sideAt} />
      <ManageMenu state={state} edge={layout.sideAt} />
      <ActivityButton label="Help" edge={layout.sideAt} onClick={() => openHelp?.()}>
        <CircleHelp aria-hidden="true" />
      </ActivityButton>
      <ActivityButton label="About Code Arena" edge={layout.sideAt} onClick={() => setAbout(true)}>
        <Info aria-hidden="true" />
      </ActivityButton>
    </nav>
  )

  const sideBar = (
    <aside key="side" data-side={layout.sideAt} hidden={!layout.sideOpen} aria-label={views.find((v) => v.id === layout.view)?.label} style={{ width: layout.side }} className="min-h-0 shrink-0 bg-sidebar text-sidebar-foreground">
      {side.map((v) => (
        <div key={v} hidden={layout.view !== v} className="h-full">
          {viewPart(v, layout.sideOpen && layout.view === v)}
        </div>
      ))}
    </aside>
  )
  const sideSplitter = layout.sideOpen && (
    <Splitter key="side-splitter" label="Resize the side bar" orientation="vertical" value={layout.side} min={bounds.side[0]} max={bounds.side[1]} grow={left ? 1 : -1} onChange={(size) => change({ side: size })} />
  )

  const panel = panelUsed && (
    // Beside the editor it gives way first when the window is narrow (the editor keeps its own minimum); below it, it keeps its height.
    <div
      key="panel"
      hidden={!layout.panelOpen}
      style={across ? { flexBasis: panelSize, minWidth: bounds.panel[0] + 20 } : { height: panelSize }}
      className={cn('flex min-w-0 flex-col', across ? cn('shrink', layout.panelAt === 'right' ? 'border-l' : 'border-r') : 'shrink-0')}
    >
      {/* Dropped after the tabs: the tab goes last, a view from the side bar comes into the panel. */}
      <div
        className={cn('flex h-7 shrink-0 items-end px-3', !across && 'border-t')}
        onDragOver={(e) => {
          if (dragged.current !== null) e.preventDefault()
        }}
        onDrop={(e) => {
          const moved = dragged.current
          dragged.current = null
          if (moved) {
            e.preventDefault()
            arrange(reorderPanel(layout, moved))
          }
        }}
      >
        <div role="tablist" aria-label="Panel" className="flex min-w-0 flex-1 items-end gap-3 overflow-x-auto text-[0.6875rem] font-semibold tracking-wider uppercase [scrollbar-width:none]">
          {layout.panelTabs.map((t) => (
            <PanelTabButton key={t} tab={t} layout={layout} change={arrange} />
          ))}
        </div>
      </div>
      <div hidden={layout.panelTab !== 'terminal'} className="min-h-0 flex-1">
        <Suspense
          fallback={
            <div className="grid h-full place-items-center">
              <Spinner label="Loading the terminal" />
            </div>
          }
        >
          <TerminalPanel shown={layout.panelOpen && layout.panelTab === 'terminal'} onHide={() => change({ panelOpen: false })} focusKey={terminalFocus} newKey={newTerminal} show={terminalShown} />
        </Suspense>
      </div>
      {layout.panelTab === 'console' && (
        <div className="min-h-0 flex-1">
          <DebugConsole />
        </div>
      )}
      {layout.panelTab === 'output' && (
        <div className="min-h-0 flex-1">
          <OutputPanel shown={layout.panelOpen} />
        </div>
      )}
      {layout.panelTabs
        .filter((t): t is View => !(panelParts as readonly string[]).includes(t))
        .map((v) => (
          <div key={v} hidden={layout.panelTab !== v} className="min-h-0 flex-1 overflow-hidden">
            {viewPart(v, layout.panelOpen && layout.panelTab === v)}
          </div>
        ))}
    </div>
  )
  const panelSplitter = layout.panelOpen && (
    <Splitter
      key="panel-splitter"
      label="Resize the panel"
      orientation={across ? 'vertical' : 'horizontal'}
      value={panelSize}
      min={bounds.panel[0]}
      max={across ? maxSidePanel : maxPanel}
      grow={layout.panelAt === 'left' ? 1 : -1}
      onChange={(size) => change({ panel: size })}
    />
  )
  const editorBox = (
    <div key="editor" className={cn('relative flex min-h-0 flex-1 flex-col', across ? 'min-w-[10rem]' : 'min-w-0')}>
      {settings.get('debug.toolBarLocation') !== 'hidden' && <DebugToolbar />}
      <EditorArea />
    </div>
  )
  const mainArea = (
    // Its parts never spill over the side bar or the chat, however narrow the window.
    <main key="main" data-panel={layout.panelAt} className={cn('relative flex min-w-0 flex-1 overflow-hidden', across ? 'flex-row' : 'flex-col')}>
      {/* By key: the panel moving round the editor moves them, never draws them again. */}
      {layout.panelAt === 'left' ? [panel, panelSplitter, editorBox] : [editorBox, panelSplitter, panel]}
    </main>
  )

  const chatSplitter = layout.chatOpen && (
    <Splitter key="chat-splitter" label="Resize the chat" orientation="vertical" value={layout.chat} min={bounds.chat[0]} max={bounds.chat[1]} grow={left ? -1 : 1} onChange={(size) => change({ chat: size })} />
  )
  const chatSide = (
    <aside key="chat" hidden={!layout.chatOpen} aria-label="Agent" style={{ width: layout.chat }} className="min-h-0 shrink-0">
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
  )

  // From the left edge with the side bar there; the other way round with it on the right.
  const workbenchParts = [activityBar, sideBar, sideSplitter, mainArea, chatSplitter, chatSide]

  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-background text-foreground">
      {!!lost && <Unreachable error={lost} />}
      {opening && (
        <output className="fixed inset-0 z-[100] grid place-items-center bg-background/90 backdrop-blur-sm">
          <span className="flex items-center gap-2 text-sm">
            <Spinner label={`Opening ${opening}`} /> Opening {opening}…
          </span>
        </output>
      )}
      {/* By key, in the order the side bar's place gives: React moves the parts, never draws the editor or the chat again. */}
      <div className="flex min-h-0 flex-1">{left ? workbenchParts : [...workbenchParts].reverse()}</div>
      <StatusBar
        state={state}
        onChanges={() => show('changes')}
        onProblems={() => show('problems')}
        onBranch={() => show('git')}
        onTerminal={toggleTerminal}
        onChat={() => change({ chatOpen: true })}
        onAbout={() => setAbout(true)}
        completing={completing}
        onCompleting={(on) => {
          void settings.update('codeArena.inlineCompletion.enabled', on).catch((e: unknown) => toast.error(e instanceof Error ? e.message : String(e)))
          if (on && !settings.get('editor.inlineSuggest.enabled')) void settings.update('editor.inlineSuggest.enabled', true).catch(() => undefined)
        }}
      />
      <About open={about} onOpenChange={setAbout} state={state} />
      {palette && <CommandPalette open={palette} onOpenChange={setPalette} editor={editorPalette()} recent={recent} onRan={ran} />}
    </div>
  )
}
