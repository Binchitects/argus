import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, FileCode2, FileDiff, FileText, GitCompareArrows, Undo2, X } from 'lucide-react'
import type * as Monaco from 'monaco-editor'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Kbd } from '@/components/ui/kbd'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { ApiError, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { asks, chatBridge, type Piece } from './bridge'
import { allCommands, onCommandsChange, runCommand } from './commands'
import { installCompletions } from './completion'
import { EditorContext, loadMonaco, rankFiles, useEditor, type DiffModels, type DiffSource, type EditorApi, type FileModel, type MonacoModule, type Reveal, type SaveReason, type Tab, type Unsaved } from './editor-state'
import { acceptChange, allFilesQuery, changesQuery, changeTexts, gitTexts, nameOf, readFile, revertChange, userFileQuery, within, writeFile, writeUserFile, type Change, type CheckProblem, type OpenedFile, type Saved, type UserFileName } from './ide-api'
import { keymap, useKeyFor } from './keyboard'
import { monacoKeybinding } from './keys'
import { saveParticipants } from './save-participants'
import { useSettings, workspaceSettingsPath } from './settings'
import { settingsSchema } from './settings-schema'
import { useMonacoTheme } from './theme'
import { diffOptionsOf, editorOptionsOf, isUserFile } from './editor-options'

/** A diff's tab: the agent's changes to a file ("diff:"), or the file's since the last commit ("git:"). */
const diffId = (path: string, source: DiffSource = 'agent') => `${source === 'git' ? 'git' : 'diff'}:${path}`

/**
 * The editor's state: the open tabs, a Monaco model per file, one code editor
 * and one diff editor that show the active tab's models. Saving sends the
 * version the file was read at, so a file changed on disk since is not
 * overwritten without asking.
 */
/** The problems the editor marks in these lines of a file (its language's, a check's), as "line:column message". */
function problemsIn(m: MonacoModule, piece: Piece): string[] {
  return m.monaco.editor
    .getModelMarkers({ resource: m.fileUri(piece.path) })
    .filter((k) => k.severity >= m.monaco.MarkerSeverity.Warning && k.startLineNumber <= piece.endLine && k.endLineNumber >= piece.startLine)
    .map((k) => `${k.startLineNumber}:${k.startColumn} ${k.message.split('\n')[0]!.replace(/\.$/, '')}`)
}

let codeActions = false

/** The editor's lines of a file (its unsaved text with them), for the light bulb's command, which every editor shares. */
const linesOf: { current: ((path: string, startLine: number, endLine: number) => Piece) | null } = { current: null }

/**
 * The editor's ways to the agent: Add to chat (Ctrl+L: the lines chosen go with the next message), and Explain, Fix and
 * Complete (asked at once), in its right-click menu and F1; and, on a line with a problem marked, "Fix with Code Arena"
 * in the light bulb.
 */
function askAboutCode(m: MonacoModule, ed: Monaco.editor.IStandaloneCodeEditor, selection: () => Piece | null) {
  const ask = (text: (p: Piece) => string) => () => {
    const piece = selection()
    if (piece) chatBridge.current?.ask(text(piece), [piece])
  }
  ed.addAction({
    id: 'codeArena.addToChat',
    label: 'Code Arena: Add to chat',
    contextMenuGroupId: '9_arena',
    contextMenuOrder: 1,
    run: () => {
      const piece = selection()
      if (piece) chatBridge.current?.attach(piece)
    },
  })
  ed.addAction({ id: 'codeArena.explain', label: 'Code Arena: Explain this', contextMenuGroupId: '9_arena', contextMenuOrder: 2, run: ask(() => asks.explain) })
  ed.addAction({ id: 'codeArena.fix', label: 'Code Arena: Fix this', contextMenuGroupId: '9_arena', contextMenuOrder: 3, run: ask((p) => asks.fix(problemsIn(m, p))) })
  ed.addAction({ id: 'codeArena.complete', label: 'Code Arena: Complete this', contextMenuGroupId: '9_arena', contextMenuOrder: 4, run: ask(() => asks.complete) })
  if (codeActions) return
  codeActions = true
  m.monaco.editor.registerCommand('arena.fixProblems', (_: unknown, path: string, line: number, endLine: number, problems: string[]) =>
    chatBridge.current?.ask(asks.fix(problems), [linesOf.current?.(path, line, endLine) ?? { path, startLine: line, endLine }]),
  )
  m.monaco.languages.registerCodeActionProvider('*', {
    provideCodeActions: (model, range, context) => {
      const path = m.pathOf(model.uri)
      const marked = context.markers.filter((k) => k.severity >= m.monaco.MarkerSeverity.Warning)
      if (!path || marked.length === 0) return { actions: [], dispose: () => undefined }
      const problems = marked.map((k) => `${k.startLineNumber}:${k.startColumn} ${k.message.split('\n')[0]!.replace(/\.$/, '')}`)
      return {
        actions: [
          {
            title: 'Fix with Code Arena',
            kind: 'quickfix',
            diagnostics: marked,
            command: { id: 'arena.fixProblems', title: 'Fix with Code Arena', arguments: [path, range.startLineNumber, range.endLineNumber, problems] },
          },
        ],
        dispose: () => undefined,
      }
    },
  })
}

/** The check's problems in a file, as the editor marks them (the word they start at, else to the line's end). */
function markProblems(m: MonacoModule, model: Monaco.editor.ITextModel, problems: CheckProblem[]) {
  const severity = { error: m.monaco.MarkerSeverity.Error, warning: m.monaco.MarkerSeverity.Warning, info: m.monaco.MarkerSeverity.Info }
  const lines = model.getLineCount()
  m.monaco.editor.setModelMarkers(
    model,
    'check',
    problems
      .filter((p) => p.line >= 1 && p.line <= lines)
      .map((p) => {
        const column = Math.min(Math.max(1, p.column), model.getLineMaxColumn(p.line))
        const word = model.getWordAtPosition({ lineNumber: p.line, column })
        return {
          startLineNumber: p.line,
          startColumn: word?.startColumn ?? column,
          endLineNumber: p.line,
          endColumn: word?.endColumn ?? model.getLineMaxColumn(p.line),
          message: p.message,
          severity: severity[p.severity],
          code: p.code ?? undefined,
          source: 'check',
        }
      }),
  )
}

async function readAny(path: string): Promise<OpenedFile> {
  if (!isUserFile(path)) return readFile(path)
  const f = await userFileQuery(path.slice(5) as UserFileName).queryFn({ signal: new AbortController().signal })
  return { path, size: f.text.length, version: f.version, text: f.text }
}

async function writeAny(path: string, text: string, version: string | null): Promise<Saved> {
  if (!isUserFile(path)) return writeFile(path, text, version)
  const saved = await writeUserFile(path.slice(5) as UserFileName, text, version)
  return { path, version: saved.version, size: text.length }
}

let keymapInstalled = false

/**
 * Monaco is given the workbench's keymap (the defaults and keybindings.json) and its commands, so the keys work alike in
 * the editor; F1 opens the workbench's palette, not Monaco's own. Given again whenever either changes.
 */
function installKeymap(m: MonacoModule) {
  if (keymapInstalled) return
  keymapInstalled = true
  let rules: { dispose(): void } | null = null
  const commands = new Map<string, { dispose(): void }>()
  let queued = false
  const sync = () => {
    queued = false
    for (const c of allCommands()) {
      if (!commands.has(c.id) && !c.id.startsWith('editor.')) commands.set(c.id, m.monaco.editor.registerCommand(c.id, (_: unknown, args: unknown) => void runCommand(c.id, args)))
    }
    rules?.dispose()
    const list = keymap.rules
      .filter((r) => r.source !== 'default' || !r.command.startsWith('editor.'))
      .map((r) => ({ keybinding: monacoKeybinding(r.chords, m.monaco), command: r.command, commandArgs: r.args, when: r.whenText }))
      .filter((r): r is { keybinding: number; command: string; commandArgs: unknown; when: string | undefined } => r.keybinding !== null)
    rules = m.monaco.editor.addKeybindingRules([{ keybinding: m.monaco.KeyCode.F1, command: '-editor.action.quickCommand' }, ...list])
    m.schemas([
      { uri: 'code-arena://schemas/settings.json', fileMatch: ['User/settings.json', '.vscode/settings.json'], schema: settingsSchema() },
      {
        uri: 'code-arena://schemas/keybindings.json',
        fileMatch: ['User/keybindings.json'],
        schema: {
          type: 'array',
          items: {
            type: 'object',
            required: ['key', 'command'],
            properties: {
              key: { type: 'string', description: 'The key or keys, as VS Code writes them: "ctrl+shift+p", "ctrl+k ctrl+s".' },
              command: { anyOf: [{ type: 'string', enum: allCommands().map((c) => c.id), enumDescriptions: allCommands().map((c) => (c.category ? `${c.category}: ${c.title}` : c.title)) }, { type: 'string' }], description: 'The command; "-command" takes away its default key.' },
              when: { type: 'string', description: 'When the key applies: context keys, !, &&, ||, ==, =~.' },
              args: { description: 'What the command is given.' },
            },
          },
        },
      },
    ])
  }
  const later = () => {
    if (queued) return
    queued = true
    queueMicrotask(sync)
  }
  keymap.subscribe(later)
  onCommandsChange(later)
  sync()
}

/** A model's indentation from the settings: the file's own when "editor.detectIndentation" is on. */
function indentModel(model: Monaco.editor.ITextModel, v: Record<string, unknown>) {
  const tabSize = v['editor.tabSize'] as number
  const insertSpaces = v['editor.insertSpaces'] as boolean
  model.updateOptions({ tabSize, insertSpaces, trimAutoWhitespace: v['editor.trimAutoWhitespace'] as boolean })
  if (v['editor.detectIndentation']) model.detectIndentation(insertSpaces, tabSize)
}

export function EditorProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const monacoTheme = useMonacoTheme()
  const settings = useSettings()
  const settingsRef = useRef(settings)
  settingsRef.current = settings
  const [tabs, setTabs] = useState<Tab[]>([])
  const [activeId, setActiveId] = useState<string | null>(null)
  const [cursor, setCursor] = useState<{ line: number; column: number } | null>(null)
  const [quickOpen, showQuickOpen] = useState(false)
  const [quickText, setQuickText] = useState('')
  const setQuickOpen = useCallback((open: boolean, typed = '') => {
    setQuickText(typed)
    showQuickOpen(open)
  }, [])
  const [unsaved, setUnsaved] = useState<{ path: string; answer: (a: Unsaved) => void } | null>(null)
  // Each open(…, at) moves the cursor, also in the tab shown already.
  const [revealed, setRevealed] = useState(0)

  // The tabs and the active one as they are now (the state renders them; the actions read these).
  const tabsRef = useRef<Tab[]>([])
  const activeRef = useRef<string | null>(null)
  const themeRef = useRef(monacoTheme)
  // Alt+Z: word wrap toggled for this editor, over the settings (null: as they say).
  const wrapOverride = useRef<string | null>(null)
  const closed = useRef<string[]>([])
  const history = useRef<{ entries: string[]; at: number }>({ entries: [], at: -1 })
  const autoSaves = useRef(new Map<string, ReturnType<typeof setTimeout>>())
  const autoSavePaused = useRef(new Set<string>())
  const mon = useRef<MonacoModule | null>(null)
  const problemsOf = useRef<CheckProblem[]>([])
  const files = useRef(new Map<string, FileModel>())
  const diffs = useRef(new Map<string, DiffModels>())
  const codeHost = useRef<HTMLDivElement | null>(null)
  const diffHost = useRef<HTMLDivElement | null>(null)
  const editor = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null)
  const diffEditor = useRef<Monaco.editor.IStandaloneDiffEditor | null>(null)
  const reveal = useRef<{ id: string; at: Reveal } | null>(null)

  const actions = useMemo(() => {
    const commit = (next: Tab[]) => {
      tabsRef.current = next
      setTabs(next)
    }
    const activate = (id: string | null, fromHistory = false) => {
      if (id && id !== activeRef.current && !fromHistory) {
        // Back and forward go through the tabs shown, in order (what came after the place gone back to is dropped).
        const h = history.current
        h.entries = [...h.entries.slice(0, h.at + 1).filter((e) => e !== id), id].slice(-50)
        h.at = h.entries.length - 1
      }
      activeRef.current = id
      setActiveId(id)
      if (!id) setCursor(null)
    }
    const update = (id: string, change: Partial<Tab>) => commit(tabsRef.current.map((t) => (t.id === id ? { ...t, ...change } : t)))
    const has = (id: string) => tabsRef.current.some((t) => t.id === id)

    const markDirty = (f: FileModel) => {
      const dirty = f.model.getAlternativeVersionId() !== f.savedAlt
      if (tabsRef.current.some((t) => t.kind === 'file' && t.path === f.path && t.dirty !== dirty)) {
        commit(tabsRef.current.map((t) => (t.kind === 'file' && t.path === f.path ? { ...t, dirty } : t)))
      }
      // "files.autoSave": "afterDelay" saves a while after the last change.
      const timer = autoSaves.current.get(f.path)
      if (timer) clearTimeout(timer)
      autoSaves.current.delete(f.path)
      const all = settingsRef.current.all()
      if (dirty && all['files.autoSave'] === 'afterDelay' && !autoSavePaused.current.has(f.path)) {
        autoSaves.current.set(
          f.path,
          setTimeout(() => {
            autoSaves.current.delete(f.path)
            void save(f.path, 'auto')
          }, all['files.autoSaveDelay'] as number),
        )
      }
    }

    const add = (tab: Tab) => {
      // Next to the tab shown, as an editor opens one.
      const ts = tabsRef.current
      const at = ts.findIndex((t) => t.id === activeRef.current)
      commit(at < 0 ? [...ts, tab] : [...ts.slice(0, at + 1), tab, ...ts.slice(at + 1)])
      activate(tab.id)
    }

    /** Forgets a tab's models. */
    const dispose = (tab: Tab) => {
      if (tab.kind === 'file') {
        closed.current = [...closed.current.filter((p) => p !== tab.path), tab.path].slice(-20)
        const f = files.current.get(tab.path)
        if (!f) return
        if (editor.current?.getModel() === f.model) editor.current.setModel(null)
        f.model.dispose()
        files.current.delete(tab.path)
      } else {
        const d = diffs.current.get(tab.id)
        if (!d) return
        if (diffEditor.current?.getModel()?.modified === d.modified) diffEditor.current.setModel(null)
        d.original.dispose()
        d.modified.dispose()
        diffs.current.delete(tab.id)
      }
    }

    /** Takes tabs away, and shows the neighbour of the one shown when it went. */
    const drop = (ids: string[]) => {
      if (ids.length === 0) return
      const before = tabsRef.current
      for (const t of before) if (ids.includes(t.id)) dispose(t)
      const left = before.filter((t) => !ids.includes(t.id))
      commit(left)
      if (activeRef.current && ids.includes(activeRef.current)) {
        const at = before.findIndex((t) => t.id === activeRef.current)
        activate(left[Math.min(at, left.length - 1)]?.id ?? null)
      }
    }

    const open = async (path: string, at?: Reveal) => {
      const id = path
      if (at) {
        reveal.current = { id, at }
        setRevealed((n) => n + 1)
      }
      if (has(id)) {
        activate(id)
        return
      }
      const load = Symbol(path)
      add({ id, kind: 'file', path, status: 'loading', dirty: false, load })
      // This open's tab as it is now: none when it was closed meanwhile (one opened again has a load of its own), renamed perhaps.
      const mine = () => tabsRef.current.find((t) => t.load === load)
      try {
        const [file, m] = await Promise.all([readAny(path), loadMonaco()])
        mon.current = m
        installKeymap(m)
        const tab = mine()
        if (!tab) return
        if (file.text === null) {
          update(tab.id, { status: file.binary ? 'binary' : 'tooLarge' })
          return
        }
        const language = m.languageOf(tab.path, settingsRef.current.get('files.associations'))
        const model = m.monaco.editor.createModel(file.text, language.id, m.fileUri(tab.path))
        indentModel(model, settingsRef.current.all(language.id))
        const f: FileModel = { path: tab.path, model, version: file.version, savedAlt: model.getAlternativeVersionId(), view: null }
        model.onDidChangeContent(() => markDirty(f))
        files.current.set(tab.path, f)
        markProblems(m, model, problemsOf.current.filter((p) => p.path === tab.path))
        update(tab.id, { status: 'ready', language: language.name, languageId: language.id })
      } catch (e) {
        const tab = mine()
        if (tab) update(tab.id, { status: 'error', message: errorMessage(e) })
      }
    }

    /** A file's change, before and now (the agent's, or since the last commit): new models, or the ones there with the texts of now. */
    const loadDiff = async (path: string, source: DiffSource = 'agent') => {
      const [texts, m] = await Promise.all([source === 'git' ? gitTexts(path) : changeTexts(path), loadMonaco()])
      mon.current = m
      installKeymap(m)
      const language = m.languageOf(path)
      const id = diffId(path, source)
      const there = diffs.current.get(id)
      if (there) {
        there.original.setValue(texts.original ?? '')
        there.modified.setValue(texts.modified ?? '')
      } else {
        diffs.current.set(id, {
          original: m.monaco.editor.createModel(texts.original ?? '', language.id, m.diffUri('before', path)),
          modified: m.monaco.editor.createModel(texts.modified ?? '', language.id, m.diffUri('after', path)),
        })
      }
      return language.name
    }

    const openDiff = async (path: string, source: DiffSource = 'agent') => {
      const id = diffId(path, source)
      if (has(id)) {
        activate(id)
        // Shown again: as the file is now.
        if (source === 'git') void loadDiff(path, source).catch(() => undefined)
        return
      }
      add({ id, kind: 'diff', source, path, status: 'loading', dirty: false })
      try {
        const language = await loadDiff(path, source)
        if (has(id)) update(id, { status: 'ready', language })
        else dispose({ id, kind: 'diff', source, path, status: 'ready', dirty: false })
      } catch (e) {
        if (has(id)) update(id, { status: 'error', message: errorMessage(e) })
      }
    }

    /**
     * Saves a file: first the save's steps for its language (trim trailing whitespace, format, the final newline…) as
     * edits Undo takes back; not for a save "afterDelay" makes as the person types. A file changed on disk asks before it
     * is written over (an auto save waits instead, and says so).
     */
    async function save(id?: string, reason: SaveReason = 'explicit'): Promise<boolean> {
      const tab = tabsRef.current.find((t) => t.id === (id ?? activeRef.current))
      const f = tab?.kind === 'file' ? files.current.get(tab.path) : undefined
      if (!f || f.model.isDisposed()) return false
      if (reason !== 'auto') {
        const m = mon.current
        const v = settingsRef.current.all(f.model.getLanguageId?.() ?? tab?.languageId)
        for (const step of saveParticipants) {
          if (!m || f.model.isDisposed()) break
          try {
            await step({ monaco: m.monaco, model: f.model, path: f.path, settings: v, reason })
          } catch (e) {
            toast.error(`Saved without ${step.what ?? 'a step'}: ${errorMessage(e)}`)
          }
        }
      }
      if (reason !== 'explicit' && f.model.getAlternativeVersionId() === f.savedAlt) return true
      const text = f.model.getValue()
      const alt = f.model.getAlternativeVersionId()
      let saved
      try {
        saved = await writeAny(f.path, text, f.version)
      } catch (e) {
        if (!(e instanceof ApiError && e.http === 409 && e.status === 'changed')) {
          toast.error(errorMessage(e))
          return false
        }
        if (reason !== 'explicit') {
          // Not written over unseen: auto save stops for this file until it is saved by hand.
          autoSavePaused.current.add(f.path)
          toast.error(`${nameOf(f.path)} changed on disk: it is not saved automatically until you save it yourself.`)
          return false
        }
        const overwrite = await confirm({
          title: `${nameOf(f.path)} changed on disk`,
          description: 'The agent or a command changed it after it was opened here. Save what the editor holds over it?',
          confirm: 'Overwrite',
          destructive: true,
        })
        if (!overwrite) return false
        try {
          saved = await writeAny(f.path, text, null)
        } catch (e2) {
          toast.error(errorMessage(e2))
          return false
        }
      }
      f.version = saved.version
      f.savedAlt = alt
      autoSavePaused.current.delete(f.path)
      markDirty(f)
      // The settings files apply as they are saved.
      if (f.path === 'user:settings.json' || f.path === workspaceSettingsPath) void settingsRef.current.reload()
      if (f.path === 'user:keybindings.json') void queryClient.invalidateQueries({ queryKey: userFileQuery('keybindings.json').queryKey })
      void queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })
      // What git sees changed since the last commit, with it.
      void queryClient.invalidateQueries({ queryKey: ['code', 'git', 'status'] })
      return true
    }

    const close = async (id: string): Promise<boolean> => {
      const tab = tabsRef.current.find((t) => t.id === id)
      if (!tab) return true
      if (tab.dirty) {
        activate(id)
        const answer = await new Promise<Unsaved>((resolve) => setUnsaved({ path: tab.path, answer: resolve }))
        setUnsaved(null)
        if (answer === 'cancel') return false
        if (answer === 'save' && !(await save(id))) return false
      }
      drop([id])
      return true
    }

    /** The tab's file as it was when a read began: still open, the same model, nothing typed in it and nothing saved since. */
    const unchanged = (t: Tab, f: FileModel, at: { alt: number; version: string }) =>
      has(t.id) && files.current.get(t.path) === f && !f.model.isDisposed() && f.model.getAlternativeVersionId() === at.alt && f.version === at.version

    /** One tab against the disk. Everything is checked again after each wait: the tab may have closed, been typed in or saved, meanwhile. */
    const refreshTab = async (t: Tab) => {
      if (t.kind === 'diff') {
        try {
          await loadDiff(t.path, t.source)
          // Closed while it loaded: its models go too.
          if (!has(t.id)) dispose(t)
        } catch {
          // Accepted or reverted elsewhere: the diff has nothing to show.
          drop([t.id])
        }
        return
      }
      const f = files.current.get(t.path)
      if (!f || f.model.isDisposed() || f.model.getAlternativeVersionId() !== f.savedAlt) return
      const at = { alt: f.savedAlt, version: f.version }
      let file
      try {
        file = await readAny(t.path)
      } catch (e) {
        // Gone (a change reverted, a file deleted by a command): a tab with nothing unsaved goes too.
        if (e instanceof ApiError && e.http === 404 && unchanged(t, f, at)) drop([t.id])
        return
      }
      // Typed in while it was read: the person's text stays, unsaved, and saving asks first (the version on disk moved on).
      // Saved while it was read: the read may be from before the save, and what was saved stays.
      if (!unchanged(t, f, at) || file.version === f.version || file.text === null) return
      if (f.model.getValue() !== file.text) {
        // An edit, not a new model: the cursor and the scroll stay, and Undo takes it back.
        f.model.pushEditOperations([], [{ range: f.model.getFullModelRange(), text: file.text }], () => null)
        f.model.pushStackElement()
      }
      f.version = file.version
      f.savedAlt = f.model.getAlternativeVersionId()
      markDirty(f)
    }

    const refresh = async (path?: string) => {
      for (const t of tabsRef.current.filter((t) => t.status === 'ready' && (path === undefined || t.path === path))) {
        try {
          await refreshTab(t)
        } catch {
          // One tab that cannot be read again leaves the others to be.
        }
      }
    }

    const moved = (from: string, to: string) => {
      const rename = (p: string) => (within(p, from) ? to + p.slice(from.length) : p)
      const m = mon.current
      for (const [p, f] of [...files.current]) {
        if (!within(p, from)) continue
        files.current.delete(p)
        f.path = rename(p)
        files.current.set(f.path, f)
        if (m) readdress(m, f)
      }
      for (const t of tabsRef.current) {
        const d = t.kind === 'diff' ? diffs.current.get(t.id) : undefined
        if (!d || !within(t.path, from)) continue
        diffs.current.delete(t.id)
        diffs.current.set(diffId(rename(t.path), t.source), d)
      }
      const ids = new Map<string, string>()
      commit(
        tabsRef.current.map((t) => {
          if (!within(t.path, from)) return t
          const path = rename(t.path)
          const id = t.kind === 'file' ? path : diffId(path, t.source)
          ids.set(t.id, id)
          const language = m && t.status === 'ready' ? m.languageOf(path, settingsRef.current.get('files.associations')) : null
          return { ...t, id, path, language: language?.name ?? t.language, languageId: language?.id ?? t.languageId }
        }),
      )
      const a = activeRef.current
      if (a && ids.has(a)) activate(ids.get(a)!)
    }

    /**
     * A file renamed: its model again at its new address (an address does not change), with its text, whether it is
     * unsaved, and where it was scrolled to. Its undo history stays with the old one.
     */
    const readdress = (m: MonacoModule, f: FileModel) => {
      const old = f.model
      const uri = m.fileUri(f.path)
      const assoc = settingsRef.current.get<Record<string, string>>('files.associations')
      if (m.monaco.editor.getModel(uri)) {
        m.monaco.editor.setModelLanguage(old, m.languageOf(f.path, assoc).id)
        return
      }
      const unsaved = old.getAlternativeVersionId() !== f.savedAlt
      const model = m.monaco.editor.createModel(old.getValue(), m.languageOf(f.path, assoc).id, uri)
      f.model = model
      f.savedAlt = unsaved ? -1 : model.getAlternativeVersionId()
      model.onDidChangeContent(() => markDirty(f))
      const ed = editor.current
      if (ed?.getModel() === old) {
        const view = ed.saveViewState()
        ed.setModel(model)
        if (view) ed.restoreViewState(view)
      }
      old.dispose()
    }

    /** The check's problems: marked in the files open now, and in those opened later. */
    const showProblems = (problems: CheckProblem[]) => {
      problemsOf.current = problems
      const m = mon.current
      if (!m) return
      for (const f of files.current.values()) markProblems(m, f.model, problems.filter((p) => p.path === f.path))
    }

    /**
     * Lines of a file as the editor has them: its text with them while it has unsaved changes (the disk's are not those),
     * else none (the file's own lines are read when they go).
     */
    const pieceOf = (path: string, startLine: number, endLine: number): Piece => {
      const tab = tabsRef.current.find((t) => t.kind === 'file' && t.path === path)
      const model = files.current.get(path)?.model
      if (!tab?.dirty || !model || model.isDisposed()) return { path, startLine, endLine }
      const last = Math.min(endLine, model.getLineCount())
      const text = startLine > last ? '' : model.getValueInRange({ startLineNumber: startLine, startColumn: 1, endLineNumber: last, endColumn: model.getLineMaxColumn(last) })
      return { path, startLine, endLine: last, text }
    }

    /** The lines chosen in the file shown (the cursor's when none are); with their text only when asked (it copies them). */
    const selection = (withText = true): Piece | null => {
      const ed = editor.current
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      const model = ed?.getModel()
      if (!ed || !model || tab?.kind !== 'file' || files.current.get(tab.path)?.model !== model) return null
      const s = ed.getSelection()
      const start = s?.startLineNumber ?? ed.getPosition()?.lineNumber ?? 1
      // A selection that ends at a line's start leaves that line out, as an editor's does.
      const end = s && s.endLineNumber > s.startLineNumber && s.endColumn === 1 ? s.endLineNumber - 1 : (s?.endLineNumber ?? start)
      return withText ? pieceOf(tab.path, start, end) : { path: tab.path, startLine: start, endLine: end }
    }

    const shownEditor = () => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      return tab?.kind === 'file' && tab.status === 'ready' ? editor.current : null
    }
    const editorCommands = () =>
      (shownEditor()?.getSupportedActions() ?? []).filter((a) => a.label).map((a) => ({ id: a.id, label: a.label }))
    const runAction = (id: string) => {
      const ed = shownEditor()
      if (!ed) return
      ed.focus()
      void ed.getAction(id)?.run()
    }

    // A tab with unsaved changes stays: its text is the person's, and saving makes the file again.
    const removed = (path: string) => drop(tabsRef.current.filter((t) => within(t.path, path) && !t.dirty).map((t) => t.id))

    const accept = async (path?: string) => {
      try {
        queryClient.setQueryData(changesQuery.queryKey, await acceptChange(path))
        drop(tabsRef.current.filter((t) => t.kind === 'diff' && t.source !== 'git' && (path === undefined || t.path === path)).map((t) => t.id))
      } catch (e) {
        toast.error(errorMessage(e))
      }
    }

    const revert = async (path: string) => {
      const change = queryClient.getQueryData<Change[]>(changesQuery.queryKey)?.find((c) => c.path === path)
      const ok = await confirm({
        title: `Revert ${nameOf(path)}?`,
        description: change?.created ? 'The agent made this file: reverting deletes it.' : 'The file goes back to how it was before the agent changed it. Its changes since are lost.',
        confirm: change?.created ? 'Delete the file' : 'Revert',
        destructive: true,
      })
      if (!ok) return
      try {
        queryClient.setQueryData(changesQuery.queryKey, await revertChange(path))
        drop(tabsRef.current.filter((t) => t.kind === 'diff' && t.source !== 'git' && t.path === path).map((t) => t.id))
        await refresh(path)
        void queryClient.invalidateQueries({ queryKey: ['code', 'files'] })
      } catch (e) {
        toast.error(errorMessage(e))
      }
    }

    const saveAll = async () => {
      for (const t of tabsRef.current.filter((x) => x.dirty)) await save(t.id)
    }

    /** Closes tabs, asking for each with unsaved changes; stops at the first the person keeps. */
    const closeMany = async (ids: string[]) => {
      for (const id of ids) if (!(await close(id))) return false
      return true
    }

    const reopenClosed = async () => {
      const path = closed.current.pop()
      if (path) await open(path)
    }

    /** The next tab (1) or the one before (-1), round. */
    const cycle = (delta: number) => {
      const ts = tabsRef.current
      if (ts.length === 0) return
      const at = ts.findIndex((t) => t.id === activeRef.current)
      activate(ts[(at + delta + ts.length) % ts.length]!.id)
    }

    const activateIndex = (index: number) => {
      const t = index < 0 ? tabsRef.current.at(-1) : tabsRef.current[index]
      if (t) activate(t.id)
    }

    /** Back or forward through the tabs shown; one closed since is skipped. */
    const navigate = (delta: -1 | 1) => {
      const h = history.current
      for (let at = h.at + delta; at >= 0 && at < h.entries.length; at += delta) {
        const id = h.entries[at]!
        if (has(id)) {
          h.at = at
          activate(id, true)
          return
        }
      }
    }

    const focusEditor = () => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      if (tab?.kind === 'file') editor.current?.focus()
      else diffEditor.current?.getModifiedEditor?.().focus()
    }

    const toggleWordWrap = () => {
      const ed = editor.current
      if (!ed) return
      const now = wrapOverride.current ?? (settingsRef.current.get<string>('editor.wordWrap', tabsRef.current.find((t) => t.id === activeRef.current)?.languageId) ?? 'off')
      wrapOverride.current = now === 'off' ? 'on' : 'off'
      ed.updateOptions({ wordWrap: wrapOverride.current as 'on' | 'off' })
    }

    /** The languages Monaco knows, for the language picker. */
    const languages = () => mon.current?.monaco.languages.getLanguages().map((l) => ({ id: l.id, name: l.aliases?.[0] ?? l.id, extensions: l.extensions ?? [] })) ?? []

    /** The file shown read as another language (until it closes). */
    const setLanguage = (id: string) => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      const f = tab?.kind === 'file' ? files.current.get(tab.path) : undefined
      const m = mon.current
      if (!tab || !f || !m) return
      m.monaco.editor.setModelLanguage(f.model, id)
      const l = m.monaco.languages.getLanguages().find((x) => x.id === id)
      indentModel(f.model, settingsRef.current.all(id))
      update(tab.id, { languageId: id, language: l?.aliases?.[0] ?? id })
    }

    /** The model of the file shown, for the status bar's indentation and line endings. */
    const activeModel = () => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      return tab?.kind === 'file' ? (files.current.get(tab.path)?.model ?? null) : null
    }

    /** Saves the files with unsaved changes when the focus leaves the editor or the window ("files.autoSave"). */
    const autoSaveOn = async (event: 'focus' | 'window') => {
      const mode = settingsRef.current.get('files.autoSave')
      if (!((mode === 'onFocusChange' && event === 'focus') || (mode === 'onWindowChange' && event === 'window') || (mode === 'onFocusChange' && event === 'window'))) return
      for (const t of tabsRef.current.filter((x) => x.dirty && x.kind === 'file' && !autoSavePaused.current.has(x.path))) await save(t.id, event)
    }

    const bindEditor = (el: HTMLDivElement | null) => {
      codeHost.current = el
    }
    const bindDiff = (el: HTMLDivElement | null) => {
      diffHost.current = el
    }

    return {
      open, openDiff, activate: (id: string) => activate(id), close, save: (id?: string) => save(id), refresh, moved, removed, accept, revert, selection, pieceOf, editorCommands, runAction, showProblems, bindEditor, bindDiff,
      saveAll, closeMany, reopenClosed, cycle, activateIndex, navigate, focusEditor, toggleWordWrap, languages, setLanguage, activeModel, autoSaveOn,
      saveWith: save,
    }
  }, [confirm, queryClient])

  const active = tabs.find((t) => t.id === activeId) ?? null
  const shown = active && active.status === 'ready' ? active.id : null

  // The editor's theme follows the settings' (and, with "window.autoDetectColorScheme", the system's light or dark).
  useEffect(() => {
    themeRef.current = monacoTheme
    mon.current?.monaco.editor.setTheme(monacoTheme)
  }, [monacoTheme])

  // The settings into the editors as they change: the file shown's language's, and each file's indentation.
  const activeLanguage = active?.languageId
  useEffect(() => {
    const m = mon.current
    if (!m) return
    editor.current?.updateOptions(editorOptionsOf(settings.all(activeLanguage), wrapOverride.current ?? undefined))
    diffEditor.current?.updateOptions(diffOptionsOf(settings.all()))
    for (const f of files.current.values()) if (!f.model.isDisposed()) indentModel(f.model, settings.all(f.model.getLanguageId?.()))
  }, [settings, activeLanguage])

  // The settings page or a command wrote a settings file: its tab shows it (when nothing in it is unsaved).
  useEffect(() => {
    const on = (e: Event) => void actions.refresh((e as CustomEvent<string>).detail === 'user' ? 'user:settings.json' : workspaceSettingsPath)
    window.addEventListener('code-arena:settings-written', on)
    const blur = () => void actions.autoSaveOn('window')
    window.addEventListener('blur', blur)
    return () => {
      window.removeEventListener('code-arena:settings-written', on)
      window.removeEventListener('blur', blur)
    }
  }, [actions])

  // The active tab into its editor, made the first time it is needed.
  useEffect(() => {
    const m = mon.current
    const tab = tabsRef.current.find((t) => t.id === shown)
    if (!tab || !m) return
    const theme = themeRef.current
    if (tab.kind === 'file') {
      const f = files.current.get(tab.path)
      if (!f || !codeHost.current) return
      if (!editor.current) {
        const ed = m.monaco.editor.create(codeHost.current, {
          model: null,
          theme,
          automaticLayout: true,
          ...editorOptionsOf(settingsRef.current.all(tab.languageId)),
          padding: { top: 6 },
          fixedOverflowWidgets: true,
        })
        ed.createContextKey?.('editorIsOpen', true)
        ed.onDidChangeCursorPosition((e) => setCursor({ line: e.position.lineNumber, column: e.position.column }))
        ed.onDidBlurEditorText?.(() => void actions.autoSaveOn('focus'))
        linesOf.current = actions.pieceOf
        askAboutCode(m, ed, actions.selection)
        keymap.setEditorCommands(ed.getSupportedActions().map((a) => a.id))
        installCompletions(m)
        editor.current = ed
      }
      const ed = editor.current
      const before = ed.getModel()
      if (before !== f.model) {
        const was = [...files.current.values()].find((x) => x.model === before)
        if (was) was.view = ed.saveViewState()
        ed.setModel(f.model)
        if (f.view) ed.restoreViewState(f.view)
        ed.updateOptions({ ariaLabel: `Editor: ${f.path}` })
      }
      const r = reveal.current
      if (r && r.id === tab.id) {
        reveal.current = null
        const column = r.at.column ?? 1
        if (r.at.endLine && r.at.endLine > r.at.line) {
          // Lines cited: selected whole, the first of them in view.
          ed.setSelection({ startLineNumber: r.at.line, startColumn: 1, endLineNumber: r.at.endLine + 1, endColumn: 1 })
        } else {
          ed.setSelection({ startLineNumber: r.at.line, startColumn: column, endLineNumber: r.at.line, endColumn: column + (r.at.length ?? 0) })
        }
        ed.revealLineInCenter(r.at.line)
      }
      const position = ed.getPosition()
      setCursor(position ? { line: position.lineNumber, column: position.column } : { line: 1, column: 1 })
      ed.focus()
    } else {
      const d = diffs.current.get(tab.id)
      if (!d || !diffHost.current) return
      if (!diffEditor.current) {
        diffEditor.current = m.monaco.editor.createDiffEditor(diffHost.current, {
          theme,
          automaticLayout: true,
          ...diffOptionsOf(settingsRef.current.all()),
          readOnly: true,
          originalEditable: false,
          useInlineViewWhenSpaceIsLimited: true,
          fixedOverflowWidgets: true,
        })
        for (const side of [diffEditor.current.getOriginalEditor?.(), diffEditor.current.getModifiedEditor?.()]) {
          side?.createContextKey('editorIsOpen', true)
          side?.createContextKey('textCompareEditorVisible', true)
        }
      }
      if (diffEditor.current.getModel()?.modified !== d.modified) diffEditor.current.setModel(d)
      setCursor(null)
    }
  }, [shown, revealed, actions])

  // Leaving with unsaved changes asks first.
  const anyDirty = tabs.some((t) => t.dirty)
  useEffect(() => {
    if (!anyDirty) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [anyDirty])

  useEffect(() => {
    const models = files.current
    const pairs = diffs.current
    return () => {
      editor.current?.dispose()
      diffEditor.current?.dispose()
      for (const f of models.values()) f.model.dispose()
      for (const d of pairs.values()) {
        d.original.dispose()
        d.modified.dispose()
      }
    }
  }, [])

  const value = useMemo<EditorApi>(
    () => ({ ...actions, tabs, active, cursor, quickOpen, setQuickOpen, quickText, setQuickText }),
    [actions, tabs, active, cursor, quickOpen, setQuickOpen, quickText],
  )
  return (
    <EditorContext value={value}>
      {children}
      <UnsavedDialog path={unsaved?.path ?? null} onAnswer={(a) => unsaved?.answer(a)} />
      <QuickOpen />
    </EditorContext>
  )
}

/** Closing a file with unsaved changes: Save, Don't save, or Cancel. */
function UnsavedDialog({ path, onAnswer }: { path: string | null; onAnswer: (a: Unsaved) => void }) {
  return (
    <Dialog open={path !== null} onOpenChange={(open) => !open && onAnswer('cancel')}>
      <DialogContent hideClose className="max-w-md">
        <DialogHeader>
          <DialogTitle>Save the changes to {path ? nameOf(path) : ''}?</DialogTitle>
          <DialogDescription>They are lost if you close it without saving.</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onAnswer('cancel')}>
            Cancel
          </Button>
          <Button variant="outline" onClick={() => onAnswer('discard')}>
            Don&apos;t save
          </Button>
          <Button onClick={() => onAnswer('save')} autoFocus>
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** Ctrl+P: any file of the working directory by a few letters of its path. */
function QuickOpen() {
  const { quickOpen, setQuickOpen, open, tabs, active, quickText: typed, setQuickText: setTyped } = useEditor()
  const all = useQuery({ ...allFilesQuery, enabled: quickOpen, staleTime: 10_000 })
  const recent = [...tabs].reverse().filter((t) => t.kind === 'file').map((t) => t.path)
  // :12 goes to line 12 of the file shown; app.ts:12 opens the file found at line 12.
  const at = /^(.*?):(\d+)(?::(\d+))?$/.exec(typed.trim())
  const name = at ? at[1]! : typed
  const line = at ? Number(at[2]) : undefined
  const column = at?.[3] ? Number(at[3]) : undefined
  const here = at && !name.trim() && active?.kind === 'file' ? active.path : null
  const shown = here ? [here] : name.trim() ? rankFiles(all.data?.files ?? [], name) : [...new Set([...recent, ...(all.data?.files ?? [])])].slice(0, 50)
  return (
    <CommandDialog
      open={quickOpen}
      onOpenChange={(o) => {
        setQuickOpen(o)
        if (!o) setTyped('')
      }}
      title="Go to file"
      description="Type a few letters of a file's path, and :line to go to a line"
      shouldFilter={false}
    >
      <CommandInput value={typed} onValueChange={setTyped} placeholder="Go to file: a few letters of its path (:12 for a line)" aria-label="File to open" />
      <CommandList>
        {all.isPending && <p className="py-6 text-center text-sm text-muted-foreground">Listing the files…</p>}
        {all.error && <p className="py-6 text-center text-sm text-muted-foreground">{errorMessage(all.error)}</p>}
        {all.data && <CommandEmpty>No file matches.</CommandEmpty>}
        {shown.map((path) => (
          <CommandItem
            key={path}
            value={path}
            onSelect={() => {
              setQuickOpen(false)
              setTyped('')
              void open(path, line ? { line, column } : undefined)
            }}
          >
            <FileText aria-hidden="true" />
            <span className="truncate font-medium">{here ? `Go to line ${line}` : nameOf(path)}</span>
            <span className="min-w-0 truncate text-xs text-muted-foreground">
              {path}
              {line ? `:${line}` : ''}
            </span>
          </CommandItem>
        ))}
        {all.data?.truncated && <p className="px-2 py-1.5 text-xs text-muted-foreground">The folder has more files than are listed here.</p>}
      </CommandList>
    </CommandDialog>
  )
}

/** The tabs, the editor or the diff of the active one, and a few words when nothing is open. */
export function EditorArea() {
  const ed = useEditor()
  const { tabs, active, bindEditor, bindDiff } = ed
  const changes = useQuery(changesQuery)
  const change = active?.kind === 'diff' && active.source !== 'git' ? changes.data?.find((c) => c.path === active.path) : undefined
  const showsFile = active?.kind === 'file' && active.status === 'ready'
  const showsDiff = active?.kind === 'diff' && active.status === 'ready'
  return (
    <section aria-label="Editor" className="flex min-h-0 min-w-0 flex-1 flex-col bg-background">
      {tabs.length > 0 && (
        <div role="tablist" aria-label="Open files" className="flex h-9 shrink-0 overflow-x-auto overflow-y-hidden border-b bg-sidebar [scrollbar-width:thin]">
          {tabs.map((t) => {
            const selected = t.id === active?.id
            const name = isUserFile(t.path) ? t.path.slice(5) : nameOf(t.path)
            return (
              <div
                key={t.id}
                role="presentation"
                className={cn(
                  'group relative flex shrink-0 items-center border-r text-[0.8125rem] transition-colors',
                  selected ? 'bg-background text-foreground after:absolute after:inset-x-0 after:top-0 after:h-0.5 after:bg-primary' : 'text-muted-foreground hover:bg-accent/60',
                )}
              >
                <button
                  type="button"
                  role="tab"
                  aria-selected={selected}
                  aria-controls="editor-panel"
                  title={t.kind === 'diff' ? `${t.path}: ${t.source === 'git' ? 'its changes since the last commit' : "the agent's changes"}` : isUserFile(t.path) ? `Your ${t.path.slice(5)} (every project)` : t.path}
                  onClick={() => ed.activate(t.id)}
                  onAuxClick={(e) => e.button === 1 && void ed.close(t.id)}
                  className="flex h-full max-w-56 items-center gap-1.5 pr-1 pl-3 outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
                >
                  {t.kind === 'diff' ? <GitCompareArrows className="size-3.5 shrink-0 text-primary" aria-hidden="true" /> : <FileCode2 className="size-3.5 shrink-0" aria-hidden="true" />}
                  <span className="truncate">{name}</span>
                  {isUserFile(t.path) && <span className="text-xs text-muted-foreground">User</span>}
                  {t.kind === 'diff' && <span className="text-xs text-muted-foreground">{t.source === 'git' ? 'since commit' : 'changes'}</span>}
                  {t.dirty && <span className="sr-only">, not saved</span>}
                </button>
                <button
                  type="button"
                  aria-label={`Close ${name}${t.kind === 'diff' ? ' changes' : ''}`}
                  title={t.dirty ? 'Not saved: close' : 'Close'}
                  onClick={() => void ed.close(t.id)}
                  className="mr-1 grid size-5 place-items-center rounded-sm outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring"
                >
                  {t.dirty ? (
                    <>
                      <span className="size-2 rounded-full bg-foreground group-hover:hidden" aria-hidden="true" />
                      <X className="hidden size-3.5 group-hover:block" aria-hidden="true" />
                    </>
                  ) : (
                    <X className={cn('size-3.5', !selected && 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100')} aria-hidden="true" />
                  )}
                </button>
              </div>
            )
          })}
        </div>
      )}
      {active && (
        <div className="flex h-8 shrink-0 items-center gap-2 border-b px-3 text-xs text-muted-foreground">
          <span className="min-w-0 truncate font-mono" title={active.path}>
            {isUserFile(active.path) ? `User › ${active.path.slice(5)}` : active.path.split('/').join(' › ')}
          </span>
          {active.kind === 'diff' && active.source === 'git' && (
            <>
              <span className="hidden shrink-0 rounded-sm bg-primary/10 px-1.5 py-0.5 text-primary-ink lg:inline" title="At the last commit, and now">
                since the last commit
              </span>
              <span className="ml-auto" />
              <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => void ed.open(active.path)}>
                <FileText /> Open file
              </Button>
            </>
          )}
          {active.kind === 'diff' && active.source !== 'git' && (
            <>
              <span className="hidden shrink-0 rounded-sm bg-primary/10 px-1.5 py-0.5 text-primary-ink lg:inline" title="Before the agent, and now">
                the agent&apos;s changes
              </span>
              {change && (
                <span className="shrink-0 font-mono tabular-nums">
                  <span className="text-success-ink">+{change.added}</span> <span className="text-destructive-ink">−{change.removed}</span>
                </span>
              )}
              <span className="ml-auto" />
              {!change?.deleted && (
                <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => void ed.open(active.path)}>
                  <FileText /> Open file
                </Button>
              )}
              <Button size="sm" variant="ghost" className="h-6 px-2 text-xs" onClick={() => void ed.revert(active.path)}>
                <Undo2 /> Revert
              </Button>
              <Button size="sm" className="h-6 px-2 text-xs" onClick={() => void ed.accept(active.path)}>
                <Check /> Accept
              </Button>
            </>
          )}
        </div>
      )}
      <div id="editor-panel" role={active ? 'tabpanel' : undefined} aria-label={active ? nameOf(active.path) : undefined} className="relative min-h-0 flex-1">
        {/* Not shown: display none (Monaco's parts set their own visibility); shown again, it lays itself out anew. */}
        <div ref={bindEditor} data-testid="code-editor" hidden={!showsFile} className="absolute inset-0" />
        <div ref={bindDiff} data-testid="diff-editor" hidden={!showsDiff} className="absolute inset-0" />
        {!active && <Welcome />}
        {active?.status === 'loading' && (
          <div className="absolute inset-0 grid place-items-center bg-background">
            <Spinner label={`Opening ${nameOf(active.path)}`} />
          </div>
        )}
        {active && (active.status === 'binary' || active.status === 'tooLarge' || active.status === 'error') && (
          <div className="absolute inset-0 grid place-items-center bg-background p-6 text-center text-sm text-muted-foreground">
            <p className="max-w-sm">
              {active.status === 'binary'
                ? `${nameOf(active.path)} is not text (or not UTF-8): it does not open in the editor.`
                : active.status === 'tooLarge'
                  ? `${nameOf(active.path)} is larger than 5 MB: it does not open in the editor.`
                  : active.message}
            </p>
          </div>
        )}
      </div>
    </section>
  )
}

function Welcome() {
  const keys: [string | undefined, string][] = [
    [useKeyFor('workbench.action.quickOpen'), 'Go to a file'],
    [useKeyFor('workbench.action.showCommands'), 'Show all commands'],
    [useKeyFor('workbench.action.findInFiles'), 'Search the files'],
    [useKeyFor('workbench.action.terminal.toggleTerminal'), 'Show or hide the terminal'],
    [useKeyFor('workbench.action.openSettings'), 'Settings'],
  ]
  return (
    <div className="absolute inset-0 grid place-items-center overflow-auto p-6">
      <div className="grid max-w-sm animate-enter justify-items-center gap-4 text-center">
        <div className="grid size-14 place-items-center rounded-2xl border bg-card shadow-sm">
          <FileDiff className="size-6 text-primary" aria-hidden="true" />
        </div>
        <div>
          <h2 className="text-base font-semibold">No file is open</h2>
          <p className="mt-1 text-sm text-muted-foreground">Open one from the Explorer, or ask the agent in the chat: what it changes shows under Agent changes.</p>
        </div>
        <dl className="grid w-full grid-cols-[auto_1fr] items-center gap-x-4 gap-y-2 text-left text-sm">
          {keys.filter((k): k is [string, string] => !!k[0]).map(([k, what]) => (
            <div key={k} className="contents">
              <dt className="justify-self-end">
                <Kbd>{k}</Kbd>
              </dt>
              <dd className="text-muted-foreground">{what}</dd>
            </div>
          ))}
        </dl>
        <p className="text-xs text-muted-foreground">The activity bar on the left switches between the Explorer, Search, Agent changes and Chat.</p>
      </div>
    </div>
  )
}
