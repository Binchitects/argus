import { useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, CircleAlert, CircleHelp, Files, GitBranch, GitCompareArrows, Info, MessagesSquare, Search, SquareTerminal, type LucideIcon } from 'lucide-react'
import { lazy, Suspense, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { toast } from '@/components/ui/toaster'
import { Alert } from '@/components/ui/alert'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { configQuery } from '@/pages/chat/api'
import type { ChatConfig } from '@/pages/chat/types'
import { stateQuery, type CodeEvent, type CodeState } from './api'
import { ChangesPanel } from './changes'
import { Sessions, ThemeMenu, Thread } from './chat'
import { useCommands, type Command } from './commands'
import { completion } from './completion'
import { CommandPalette, recentLimit, type PaletteCommand } from './palette'
import { ProblemsPanel } from './problems'
import { SourceControlPanel } from './scm'
import { EditorArea, EditorProvider } from './editor'
import { userFile } from './editor-options'
import { CodeRefsProvider } from './refs'
import { useEditor } from './editor-state'
import { Explorer } from './explorer'
import { HelpProvider } from './help'
import { useOpenHelp } from './help-context'
import { changesQuery, folderQuery, gitStatusQuery, parentOf, preferencesQuery, problemsQuery, savePreferences, writeFile } from './ide-api'
import { setContext, useKeyboard, useKeyFor } from './keyboard'
import { quickPick } from './quick-pick'
import { QuickPickHost } from './quick-pick-host'
import { SearchPanel } from './search'
import { SettingsProvider, useSettings, workspaceSettingsPath } from './settings'
import { colorThemes, themeNamed } from './settings-schema'
import { useThemeSync } from './theme'
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
  // The layout as it was left, in any run: waited for, so the workbench opens as it was.
  const preferences = useQuery(preferencesQuery)

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
    <SettingsProvider>
      <EditorProvider>
        <HelpProvider manual={state.data.manual}>
          <CodeRefsProvider folder={state.data.folder}>
            <SettingsRead>
              <Workbench state={state.data} config={config.data} lost={state.error ?? config.error} saved={preferences.data?.layout} recentSaved={preferences.data?.paletteRecent} />
            </SettingsRead>
            <QuickPickHost />
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

const views: { id: View; label: string; icon: LucideIcon; command: string }[] = [
  { id: 'explorer', label: 'Explorer', icon: Files, command: 'workbench.view.explorer' },
  { id: 'search', label: 'Search', icon: Search, command: 'workbench.action.findInFiles' },
  { id: 'git', label: 'Source control', icon: GitBranch, command: 'workbench.view.scm' },
  { id: 'changes', label: 'Agent changes', icon: GitCompareArrows, command: 'codeArena.view.changes' },
  { id: 'problems', label: 'Problems', icon: CircleAlert, command: 'workbench.actions.view.problems' },
  { id: 'chat', label: 'Chat', icon: MessagesSquare, command: 'codeArena.view.sessions' },
]

function ActivityButton({ label, command, pressed, badge, onClick, children }: { label: string; command?: string; pressed?: boolean; badge?: number; onClick: () => void; children: ReactNode }) {
  const keys = useKeyFor(command ?? '')
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
  const { refresh, setQuickOpen, openDiff, accept, editorCommands, runAction, showProblems, active } = ed
  const settings = useSettings()
  const [palette, setPalette] = useState(false)
  const [recent, setRecent] = useState<string[]>(() => (Array.isArray(recentSaved) ? recentSaved.filter((x): x is string => typeof x === 'string') : []))
  useThemeSync()
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
  const openHelp = useOpenHelp()
  // Bumped to put the focus in the search box or the terminal.
  const [searchFocus, setSearchFocus] = useState(0)
  const [replaceKey, setReplaceKey] = useState(0)
  const [terminalFocus, setTerminalFocus] = useState(0)
  const [newTerminal, setNewTerminal] = useState(0)
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
  const copy = (text: string) =>
    void navigator.clipboard?.writeText(text).then(
      () => toast.success('Copied.'),
      () => toast.error('The browser did not let the page copy.'),
    )
  const pickTheme = async () => {
    const now = settings.get<string>('workbench.colorTheme')
    const picked = await quickPick({
      title: 'Color theme',
      placeholder: 'Select a color theme',
      active: now,
      items: colorThemes.map((t) => ({ id: t.name, label: t.name, description: t.name === themeNamed(now).name ? 'current' : undefined, group: t.kind === 'light' ? 'light themes' : 'dark themes' })),
    })
    if (!picked) return
    try {
      await settings.update('workbench.colorTheme', picked.id)
      if (settings.get('window.autoDetectColorScheme')) await settings.update('window.autoDetectColorScheme', false)
    } catch (e) {
      toast.error(e instanceof Error ? e.message : String(e))
    }
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
  const workbenchCommands: Command[] = [
    { id: 'workbench.action.showCommands', category: 'View', title: 'Show All Commands', hidden: true, run: () => setPalette(true) },
    { id: 'workbench.action.quickOpen', category: 'File', title: 'Go to File…', run: () => setQuickOpen(true) },
    { id: 'workbench.action.gotoLine', category: 'Go', title: 'Go to Line/Column…', enabled: () => file, run: () => setQuickOpen(true, ':') },
    { id: 'workbench.action.gotoSymbol', category: 'Go', title: 'Go to Symbol in Editor…', enabled: () => file, run: () => runAction('editor.action.quickOutline') },
    { id: 'workbench.action.files.save', category: 'File', title: 'Save', enabled: () => file, run: () => void ed.save() },
    { id: 'workbench.action.files.saveAll', category: 'File', title: 'Save All', run: () => void ed.saveAll() },
    { id: 'workbench.action.closeActiveEditor', category: 'View', title: 'Close Editor', enabled: () => !!active, run: () => active && void ed.close(active.id) },
    { id: 'workbench.action.closeAllEditors', category: 'View', title: 'Close All Editors', run: () => void ed.closeMany(ed.tabs.map((t) => t.id)) },
    { id: 'workbench.action.closeUnmodifiedEditors', category: 'View', title: 'Close Saved Editors', run: () => void ed.closeMany(ed.tabs.filter((t) => !t.dirty).map((t) => t.id)) },
    { id: 'workbench.action.closeOtherEditors', category: 'View', title: 'Close Other Editors', run: () => void ed.closeMany(ed.tabs.filter((t) => t.id !== active?.id).map((t) => t.id)) },
    { id: 'workbench.action.reopenClosedEditor', category: 'View', title: 'Reopen Closed Editor', run: () => void ed.reopenClosed() },
    { id: 'workbench.action.nextEditor', category: 'View', title: 'Open Next Editor', run: () => ed.cycle(1) },
    { id: 'workbench.action.previousEditor', category: 'View', title: 'Open Previous Editor', run: () => ed.cycle(-1) },
    ...Array.from({ length: 9 }, (_, i): Command => ({ id: `workbench.action.openEditorAtIndex${i + 1}`, category: 'View', title: i === 8 ? 'Open Last Editor in Group' : `Open Editor at Index ${i + 1}`, hidden: true, run: () => ed.activateIndex(i === 8 ? -1 : i) })),
    { id: 'workbench.action.navigateBack', category: 'Go', title: 'Go Back', run: () => ed.navigate(-1) },
    { id: 'workbench.action.navigateForward', category: 'Go', title: 'Go Forward', run: () => ed.navigate(1) },
    { id: 'workbench.action.files.copyPathOfActiveFile', category: 'File', title: 'Copy Path of Active File', enabled: () => !!active, run: () => active && copy(`${state.folder}/${active.path}`) },
    { id: 'copyRelativeFilePath', category: 'File', title: 'Copy Relative Path of Active File', enabled: () => !!active, run: () => active && copy(active.path) },
    { id: 'workbench.action.editor.changeLanguageMode', category: 'Change Language Mode', title: 'Change Language Mode', enabled: () => file, run: () => void pickLanguage() },
    { id: 'editor.action.toggleWordWrap', category: 'View', title: 'Toggle Word Wrap', enabled: () => file, run: () => ed.toggleWordWrap() },
    { id: 'workbench.action.focusFirstEditorGroup', category: 'View', title: 'Focus First Editor Group', run: () => ed.focusEditor() },
    { id: 'workbench.action.focusSideBar', category: 'View', title: 'Focus into Primary Side Bar', run: () => {
      change({ sideOpen: true })
      requestAnimationFrame(() => document.querySelector<HTMLElement>('aside[data-side] [tabindex="0"], aside[data-side] button, aside[data-side] input')?.focus())
    } },
    { id: 'workbench.action.toggleSidebarVisibility', category: 'View', title: 'Toggle Primary Side Bar Visibility', run: () => change({ sideOpen: !layout.sideOpen }) },
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
    { id: 'codeArena.view.sessions', category: 'View', title: 'Show the Chat Sessions', run: () => show('chat') },
    { id: 'codeArena.acceptAllChanges', category: 'Code Arena', title: "Accept All the Agent's Changes", enabled: () => count > 0, run: () => void accept() },
    { id: 'workbench.action.terminal.toggleTerminal', category: 'View', title: 'Toggle Terminal', run: togglePanel },
    { id: 'workbench.action.terminal.new', category: 'Terminal', title: 'Create New Terminal', run: () => {
      setPanelUsed(true)
      change({ panelOpen: true })
      setNewTerminal((n) => n + 1)
    } },
    { id: 'workbench.action.openSettings', category: 'Preferences', title: 'Open Settings', run: () => void ed.open(userFile('settings.json')) },
    { id: 'workbench.action.openSettingsJson', category: 'Preferences', title: 'Open User Settings (JSON)', run: () => void ed.open(userFile('settings.json')) },
    { id: 'workbench.action.openWorkspaceSettingsFile', category: 'Preferences', title: 'Open Workspace Settings (JSON)', run: () => void openWorkspaceSettings() },
    { id: 'workbench.action.openGlobalKeybindings', category: 'Preferences', title: 'Open Keyboard Shortcuts', run: () => void ed.open(userFile('keybindings.json')) },
    { id: 'workbench.action.openGlobalKeybindingsFile', category: 'Preferences', title: 'Open Keyboard Shortcuts (JSON)', run: () => void ed.open(userFile('keybindings.json')) },
    { id: 'workbench.action.selectTheme', category: 'Preferences', title: 'Color Theme', run: () => void pickTheme() },
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
  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-background text-foreground">
      {!!lost && <Unreachable error={lost} />}
      <div className="flex min-h-0 flex-1">
        <nav aria-label="Activity bar" className="flex w-12 shrink-0 flex-col border-r bg-sidebar">
          {views.map((v) => (
            <ActivityButton key={v.id} label={v.label} command={v.command} pressed={layout.sideOpen && layout.view === v.id} badge={v.id === 'changes' ? count : v.id === 'problems' ? errors : v.id === 'git' ? gitCount : undefined} onClick={() => show(v.id, true)}>
              <v.icon aria-hidden="true" />
            </ActivityButton>
          ))}
          <span className="flex-1" />
          <ActivityButton label="Terminal" command="workbench.action.terminal.toggleTerminal" pressed={layout.panelOpen} onClick={togglePanel}>
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

        <aside data-side hidden={!layout.sideOpen} aria-label={views.find((v) => v.id === layout.view)?.label} style={{ width: layout.side }} className="min-h-0 shrink-0 bg-sidebar text-sidebar-foreground">
          <div hidden={layout.view !== 'explorer'} className="h-full">
            <Explorer project={state.project} onOpenChanges={(path) => void openDiff(path)} />
          </div>
          <div hidden={layout.view !== 'search'} className="h-full">
            <SearchPanel focusKey={searchFocus} replaceKey={replaceKey} />
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
                <TerminalPanel shown={layout.panelOpen} onHide={() => change({ panelOpen: false })} focusKey={terminalFocus} newKey={newTerminal} />
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
          void settings.update('codeArena.inlineCompletion.enabled', on).catch((e: unknown) => toast.error(e instanceof Error ? e.message : String(e)))
          if (on && !settings.get('editor.inlineSuggest.enabled')) void settings.update('editor.inlineSuggest.enabled', true).catch(() => undefined)
        }}
      />
      <About open={about} onOpenChange={setAbout} state={state} />
      {palette && <CommandPalette open={palette} onOpenChange={setPalette} editor={editorPalette()} recent={recent} onRan={ran} />}
    </div>
  )
}
