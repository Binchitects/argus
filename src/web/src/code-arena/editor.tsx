import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Eye, FileCode2, FileDiff, FileImage, FileText, FolderOpen, GitCompareArrows, Keyboard, Settings2, X } from 'lucide-react'
import type * as Monaco from 'monaco-editor'
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ContextMenu } from 'radix-ui'
import { Button } from '@/components/ui/button'
import { CommandDialog, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { menuContent, menuItem } from '@/components/ui/dropdown-menu'
import { Kbd } from '@/components/ui/kbd'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { ApiError, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { asks, chatBridge, type Piece } from './bridge'
import { Markdown } from '@/pages/chat/markdown'
import { allCommands, onCommandsChange, runCommand } from './commands'
import { installCompletions } from './completion'
import { compareId, renamed, revertChanges, sideLabel, sidePath, takeChanges, touched, type LineChange, type Side } from './compare'
import { CompareToolbar } from './compare-toolbar'
import { EditorContext, loadMonaco, mediaKind, rankFiles, useEditor, type DiffModels, type DiffSource, type EditorApi, type FileModel, type MonacoModule, type Reveal, type SaveReason, type Tab, type Unsaved } from './editor-state'
import { acceptChange, allFilesQuery, changesQuery, changeTexts, gitShow, gitStageText, gitStatusQuery, nameOf, rawFileUrl, readFile, revertChange, symbolsQuery, userFileQuery, within, writeFile, writeUserFile, type Change, type CheckProblem, type OpenedFile, type Saved, type UserFileName } from './ide-api'
import { addDebugActions, installDebugEditor } from './debug/editor-debug'
import { installExtensions } from './extensions'
import { bindEditorContext, keymap, useKeyFor } from './keyboard'
import { monacoDefaults, monacoKeybinding } from './keys'
import { installFormatting, lineEdit, minimalEdit, offerFormatting } from './format'
import { installDirtyDiff, type DiffDecorations } from './dirty-diff'
import { saveParticipants } from './save-participants'
import { useSettings, workspaceSettingsPath } from './settings'
import { settingsSchema } from './settings-schema'
import { useMonacoTheme } from './theme'
import { diffOptionsOf, editorOptionsOf, isUserFile } from './editor-options'
import { languageServers } from './lsp/servers'

/** An editor action's own id: Monaco lists the ones added to an editor under that editor's id first. */
const actionId = (id: string) => id.replace(/^vs\.editor\.ICodeEditor:\d+:/, '')

/** A diff's tab: the agent's changes to a file ("diff:"), or the file's since the last commit ("git:"). */
const diffId = (path: string, source: DiffSource = 'agent') => `${source === 'git' ? 'git' : source === 'staged' ? 'staged' : 'diff'}:${path}`

/** The files a tab holds in the editor: a file tab's own, a compare's live sides. */
const fileSidesOf = (t: Tab): string[] =>
  t.kind === 'file' || t.kind === 'preview' ? [t.path] : [t.left, t.right].flatMap((x) => (x?.kind === 'file' ? [x.path] : []))

/** A compare tab's id for its sides: the agent's and git's as they were, any other by its sides. */
const idOf = (t: Pick<Tab, 'source' | 'path' | 'left' | 'right'>) => (t.source === 'compare' || !t.source ? compareId(t.left!, t.right!) : diffId(t.path, t.source))

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

// The workbench's own pages, loaded the first time they open.
const SettingsEditor = lazy(() => import('./settings-editor'))
const KeybindingsEditor = lazy(() => import('./keybindings-editor'))

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
    // Monaco's own commands stay its own; the workbench's (editor.action.toggleWordWrap, the debugger's…) are given to it.
    const own = keymap.editorCommands
    for (const c of allCommands()) {
      if (!commands.has(c.id) && !own.has(c.id)) commands.set(c.id, m.monaco.editor.registerCommand(c.id, (_: unknown, args: unknown) => void runCommand(c.id, args)))
    }
    rules?.dispose()
    // Monaco's own defaults are in the keymap for the lists: Monaco has them already.
    const list = keymap.rules
      .filter((r) => r.source !== 'editor')
      .map((r) => ({ keybinding: monacoKeybinding(r.chords, m.monaco), command: r.command, commandArgs: r.args, when: r.whenText }))
      .filter((r): r is { keybinding: number; command: string; commandArgs: unknown; when: string | undefined } => r.keybinding !== null)
    rules = m.monaco.editor.addKeybindingRules([{ keybinding: m.monaco.KeyCode.F1, command: '-editor.action.quickCommand' }, ...list])
    // The editor's own way to its palette (its context menu's Command Palette) opens the workbench's: the newest command wins.
    commands.get('editor.action.quickCommand')?.dispose()
    commands.set('editor.action.quickCommand', m.monaco.editor.registerCommand('editor.action.quickCommand', () => void runCommand('workbench.action.showCommands')))
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

/** The indentation settings each model was given last, so another setting changing leaves what was chosen for it alone. */
const indented = new WeakMap<Monaco.editor.ITextModel, string>()

/**
 * A model's indentation from the settings: the file's own when "editor.detectIndentation" is on. With `always` false, only
 * when those settings changed since they were given to it: the indentation chosen for a file (Indent Using Tabs in the
 * status bar) stays as long as they do not.
 */
function indentModel(model: Monaco.editor.ITextModel, v: Record<string, unknown>, always = true) {
  const tabSize = v['editor.tabSize'] as number
  const insertSpaces = v['editor.insertSpaces'] as boolean
  const trimAutoWhitespace = v['editor.trimAutoWhitespace'] as boolean
  const detect = !!v['editor.detectIndentation']
  const given = JSON.stringify([tabSize, insertSpaces, trimAutoWhitespace, detect])
  if (!always && indented.get(model) === given) return
  indented.set(model, given)
  model.updateOptions({ tabSize, insertSpaces, trimAutoWhitespace })
  if (detect) model.detectIndentation(insertSpaces, tabSize)
}

/** "files.eol" for a file with no text yet (a new one): its line ending, the system's with "auto". */
function newFileEol(m: MonacoModule, v: Record<string, unknown>) {
  const eol = v['files.eol'] === 'auto' ? (typeof navigator !== 'undefined' && navigator.platform.startsWith('Win') ? '\r\n' : '\n') : v['files.eol']
  return eol === '\r\n' ? m.monaco.editor.EndOfLineSequence.CRLF : m.monaco.editor.EndOfLineSequence.LF
}

export function EditorProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const monacoTheme = useMonacoTheme()
  const settings = useSettings()
  const settingsRef = useRef(settings)
  useLayoutEffect(() => {
    settingsRef.current = settings
  })
  const [tabs, setTabs] = useState<Tab[]>([])
  const [activeId, setActiveId] = useState<string | null>(null)
  const [cursor, setCursor] = useState<{ line: number; column: number } | null>(null)
  const [quickOpen, showQuickOpen] = useState(false)
  const [quickText, setQuickText] = useState('')
  const setQuickOpen = useCallback((open: boolean, typed = '') => {
    setQuickText(typed)
    showQuickOpen(open)
  }, [])
  const [unsaved, setUnsaved] = useState<{ paths: string[]; answer: (a: Unsaved) => void } | null>(null)
  const [pageQuery, setPageQuery] = useState('')
  // Each open(…, at) moves the cursor, also in the tab shown already.
  const [revealed, setRevealed] = useState(0)
  const [diffMade, setDiffMade] = useState(false)

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

    /** Whether a file the editor holds has unsaved changes. */
    const fileDirty = (path: string) => {
      const f = files.current.get(path)
      return !!f && !f.model.isDisposed() && f.model.getAlternativeVersionId() !== f.savedAlt
    }
    const markDirty = (f: FileModel) => {
      const dirty = f.model.getAlternativeVersionId() !== f.savedAlt
      // Every tab that holds the file: its own, and the compares it is a side of.
      const next = tabsRef.current.map((t) => {
        const sides = fileSidesOf(t)
        if (!sides.includes(f.path)) return t
        const d = sides.some((p) => (p === f.path ? dirty : fileDirty(p)))
        return t.dirty === d ? t : { ...t, dirty: d }
      })
      if (next.some((t, i) => t !== tabsRef.current[i])) commit(next)
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
            void saveFile(f, 'auto')
          }, all['files.autoSaveDelay'] as number),
        )
      }
    }

    const add = (tab: Tab, show = true) => {
      // Next to the tab shown, as an editor opens one.
      const ts = tabsRef.current
      const at = ts.findIndex((t) => t.id === activeRef.current)
      commit(at < 0 ? [...ts, tab] : [...ts.slice(0, at + 1), tab, ...ts.slice(at + 1)])
      if (show) activate(tab.id)
    }

    /** The diff editor lets go of a model about to be disposed of. */
    const unshow = (model: Monaco.editor.ITextModel | null) => {
      if (!model) return
      if (editor.current?.getModel() === model) editor.current.setModel(null)
      const shown = diffEditor.current?.getModel()
      if (shown && (shown.original === model || shown.modified === model)) diffEditor.current!.setModel(null)
    }

    /** Forgets a compare's own models. */
    const disposeDiff = (id: string) => {
      const d = diffs.current.get(id)
      if (!d) return
      for (const model of [d.left, d.right]) {
        unshow(model)
        model?.dispose()
      }
      diffs.current.delete(id)
    }

    /**
     * Forgets the files no tab holds any more (a file is held by its tab and by the compares it is a side of). One with
     * unsaved changes is never thrown away unasked (a compare closed or dropped that was the only tab holding it): it stays
     * open in a tab of its own, but those the person chose not to save.
     */
    const collect = (discarded: string[] = []) => {
      const held = new Set(tabsRef.current.flatMap(fileSidesOf))
      const kept: Tab[] = []
      for (const [path, f] of [...files.current]) {
        if (held.has(path)) continue
        if (fileDirty(path) && !discarded.includes(path)) {
          kept.push({ id: path, kind: 'file', path, status: 'ready', dirty: true, language: languageName(f.model), languageId: f.model.getLanguageId?.() })
          continue
        }
        unshow(f.model)
        f.model.dispose()
        files.current.delete(path)
      }
      if (kept.length > 0) commit([...tabsRef.current, ...kept])
    }

    /** Takes tabs away, and shows the neighbour of the one shown when it went (the files' changes discarded are thrown away). */
    const drop = (ids: string[], discarded: string[] = []) => {
      if (ids.length === 0) return
      const before = tabsRef.current
      for (const t of before) {
        if (!ids.includes(t.id)) continue
        if (t.kind === 'file') closed.current = [...closed.current.filter((p) => p !== t.path), t.path].slice(-20)
        else disposeDiff(t.id)
      }
      const left = before.filter((t) => !ids.includes(t.id))
      commit(left)
      collect(discarded)
      if (activeRef.current && ids.includes(activeRef.current)) {
        const at = before.findIndex((t) => t.id === activeRef.current)
        activate(left[Math.min(at, left.length - 1)]?.id ?? null)
      }
    }

    /** Opens a file in its tab (at a place); behind the tab shown, not shown, with `behind`. */
    const open = async (path: string, at?: Reveal, behind = false) => {
      const id = path
      if (at && !behind) {
        reveal.current = { id, at }
        setRevealed((n) => n + 1)
      }
      if (has(id)) {
        if (behind) await loadFile(path).catch(() => undefined)
        else activate(id)
        return
      }
      if (mediaKind(path)) {
        // Shown as it is, from its address: never read as text.
        add({ id, kind: 'media', path, status: 'ready', dirty: false, mediaVersion: 0 }, !behind)
        return
      }
      const load = Symbol(path)
      add({ id, kind: 'file', path, status: 'loading', dirty: false, load }, !behind)
      // This open's tab as it is now: none when it was closed meanwhile (one opened again has a load of its own), renamed perhaps.
      const mine = () => tabsRef.current.find((t) => t.load === load)
      try {
        let f = await loadFile(path)
        const tab = mine()
        if (!tab) {
          collect()
          return
        }
        if (typeof f === 'string') {
          update(tab.id, { status: f })
          return
        }
        // Renamed while it was read: the model goes to the file's new name.
        if (tab.path !== path && files.current.get(path) === f && !files.current.has(tab.path) && mon.current) {
          files.current.delete(path)
          f.path = tab.path
          files.current.set(tab.path, f)
          readdress(mon.current, f)
          f = files.current.get(tab.path)!
        }
        const language = { id: f.model.getLanguageId?.() ?? 'plaintext', name: languageName(f.model) }
        update(tab.id, { status: 'ready', language: language.name, languageId: language.id, dirty: fileDirty(tab.path) })
      } catch (e) {
        const tab = mine()
        if (tab) update(tab.id, { status: 'error', message: errorMessage(e) })
      }
    }

    const languageName = (model: Monaco.editor.ITextModel) => {
      const id = model.getLanguageId?.()
      const l = mon.current?.monaco.languages.getLanguages().find((x) => x.id === id)
      return l?.aliases?.[0] ?? id ?? 'Plain Text'
    }

    const loading = new Map<string, Promise<FileModel | 'binary' | 'tooLarge'>>()
    /** A file's model, read once however many tabs ask for it at once (the editor holds one model per file). */
    const loadFile = (path: string): Promise<FileModel | 'binary' | 'tooLarge'> => {
      const there = files.current.get(path)
      if (there && !there.model.isDisposed()) return Promise.resolve(there)
      const going = loading.get(path)
      if (going) return going
      const read = (async () => {
        const [file, m] = await Promise.all([readAny(path), loadMonaco()])
        mon.current = m
        installKeymap(m)
        installExtensions(m)
        languageServers.attach(m)
        if (file.text === null) return file.binary ? 'binary' : 'tooLarge'
        const now = files.current.get(path)
        if (now && !now.model.isDisposed()) return now
        const newline = file.text.indexOf('\n')
        const language = m.languageOf(path, settingsRef.current.get('files.associations'), newline < 0 ? file.text : file.text.slice(0, newline))
        const model = m.monaco.editor.createModel(file.text, language.id, m.fileUri(path))
        const v = settingsRef.current.all(language.id)
        indentModel(model, v)
        // A file with no line yet has no line ending of its own: "files.eol" gives it one.
        if (file.text === '') model.setEOL(newFileEol(m, v))
        const f: FileModel = { path, model, version: file.version, savedAlt: model.getAlternativeVersionId(), view: null }
        model.onDidChangeContent(() => markDirty(f))
        files.current.set(path, f)
        markProblems(m, model, problemsOf.current.filter((p) => p.path === path))
        installFormatting({ m, queryClient, settings: (language) => settingsRef.current.all(language) })
        if (!isUserFile(path)) void offerFormatting(path, language.id)
        return f
      })()
      loading.set(path, read)
      const forget = () => {
        if (loading.get(path) === read) loading.delete(path)
      }
      read.then(forget, forget)
      return read
    }

    /** A side's text, when it is no file the editor holds. */
    const sideText = async (side: Side): Promise<string> => {
      switch (side.kind) {
        case 'disk': {
          const f = await readAny(side.path)
          if (f.text === null) throw new Error(`${nameOf(side.path)} is not text: it cannot be compared.`)
          return f.text
        }
        case 'git': {
          const shown = await gitShow(side.path, side.rev)
          if (shown.binary || shown.tooLarge) throw new Error(`${nameOf(side.path)} at ${side.rev === ':' ? 'the index' : side.rev} is ${shown.binary ? 'not text' : 'larger than 5 MB'}: it cannot be compared.`)
          return shown.text ?? ''
        }
        case 'agent':
          return (await changeTexts(side.path)).original ?? ''
        case 'clipboard':
          return side.text
        default:
          return ''
      }
    }

    /**
     * A compare's sides as models: a file's own model (live: typing in it is typing in the file), a model of the text for
     * any other side (made, or given the text of now). Its language is its file's.
     */
    const loadSides = async (id: string) => {
      const tab = tabsRef.current.find((t) => t.id === id)
      if (!tab?.left || !tab.right) throw new Error('Nothing to compare.')
      const m = await loadMonaco()
      mon.current = m
      installKeymap(m)
      installExtensions(m)
      const path = sidePath(tab.right) ?? sidePath(tab.left) ?? ''
      const language = m.languageOf(path, settingsRef.current.get('files.associations'))
      const own: DiffModels = diffs.current.get(id) ?? { left: null, right: null }
      for (const which of ['left', 'right'] as const) {
        const side = tab[which]!
        if (side.kind === 'file') {
          const f = await loadFile(side.path)
          if (typeof f === 'string') throw new Error(`${nameOf(side.path)} is ${f === 'binary' ? 'not text' : 'larger than 5 MB'}: it cannot be compared.`)
          if (own[which]) {
            unshow(own[which])
            own[which]!.dispose()
            own[which] = null
          }
          continue
        }
        const text = await sideText(side)
        if (own[which] && !own[which]!.isDisposed()) {
          if (own[which]!.getValue() !== text) own[which]!.setValue(text)
        } else own[which] = m.monaco.editor.createModel(text, language.id, m.diffUri(which === 'left' ? 'before' : 'after', path))
      }
      diffs.current.set(id, own)
      return language
    }

    /** The two models a compare tab shows, left and right. */
    const modelsOf = (tab: Tab): { original: Monaco.editor.ITextModel; modified: Monaco.editor.ITextModel } | null => {
      const own = diffs.current.get(tab.id)
      const of = (side: Side | undefined, mine: Monaco.editor.ITextModel | null | undefined) => (side?.kind === 'file' ? files.current.get(side.path)?.model : mine) ?? null
      const original = of(tab.left, own?.left)
      const modified = of(tab.right, own?.right)
      return original && modified && !original.isDisposed() && !modified.isDisposed() ? { original, modified } : null
    }

    /**
     * A compare into the diff editor: a file's side is the file itself (typed in, it is unsaved: Ctrl+S saves it); a side
     * of a text (a commit, the clipboard, before the agent) is read-only. The arrows beside a change put the left side's
     * lines back.
     */
    const present = (tab: Tab) => {
      const de = diffEditor.current
      const d = modelsOf(tab)
      if (!de || !d) return
      de.updateOptions({ originalEditable: tab.left?.kind === 'file', readOnly: tab.right?.kind !== 'file', renderMarginRevertIcon: tab.right?.kind === 'file' })
      const now = de.getModel()
      if (now?.original !== d.original || now.modified !== d.modified) de.setModel(d)
    }

    /** Opens a compare of two sides; one open already is shown, as it is now. */
    const openCompare = async (tab: Omit<Tab, 'id' | 'status' | 'dirty'>) => {
      const id = idOf(tab)
      if (has(id)) {
        activate(id)
        void reloadCompare(id)
        return
      }
      add({ ...tab, id, status: 'loading', dirty: false })
      try {
        const language = await loadSides(id)
        if (has(id)) update(id, { status: 'ready', language: language.name, languageId: language.id, dirty: fileSidesOf(tabsRef.current.find((t) => t.id === id)!).some(fileDirty) })
        else {
          disposeDiff(id)
          collect()
        }
      } catch (e) {
        if (has(id)) update(id, { status: 'error', message: errorMessage(e) })
      }
    }

    const compare = (left: Side, right: Side) => openCompare({ kind: 'diff', source: 'compare', left, right, path: sidePath(right) ?? sidePath(left) ?? '' })

    /**
     * A change's sides, as VS Code's: the agent's (before its first change ↔ the file), a file's not staged (the index ↔
     * the file; nothing on the left for a file git does not know), its staged ones (HEAD ↔ the index; a rename's old name
     * on the left). A side made or deleted is empty.
     */
    const sidesOf = async (path: string, source: DiffSource): Promise<{ left: Side; right: Side }> => {
      if (source === 'agent') {
        const texts = await changeTexts(path)
        return { left: texts.original === null ? { kind: 'empty', path } : { kind: 'agent', path }, right: texts.modified === null ? { kind: 'empty', path } : { kind: 'file', path } }
      }
      const status = await queryClient.fetchQuery({ ...gitStatusQuery, staleTime: 1_000 })
      const f = status.files?.find((x) => x.path === path)
      if (source === 'staged') {
        const left: Side = f?.staged === 'A' ? { kind: 'empty', path } : { kind: 'git', path: f?.staged === 'R' && f.from ? f.from : path, rev: 'HEAD' }
        return { left, right: f?.staged === 'D' ? { kind: 'empty', path } : { kind: 'git', path, rev: ':' } }
      }
      return { left: f?.changed === '?' ? { kind: 'empty', path } : { kind: 'git', path, rev: ':' }, right: f?.changed === 'D' ? { kind: 'empty', path } : { kind: 'file', path } }
    }

    const openDiff = async (path: string, source: DiffSource = 'agent') => {
      const id = diffId(path, source)
      if (has(id)) {
        activate(id)
        void reloadCompare(id)
        return
      }
      add({ id, kind: 'diff', source, path, status: 'loading', dirty: false })
      try {
        const sides = await sidesOf(path, source)
        if (!has(id)) return
        update(id, sides)
        const language = await loadSides(id)
        if (has(id)) update(id, { status: 'ready', language: language.name, languageId: language.id, dirty: fileSidesOf(tabsRef.current.find((t) => t.id === id)!).some(fileDirty) })
        else {
          disposeDiff(id)
          collect()
        }
      } catch (e) {
        if (has(id)) update(id, { status: 'error', message: errorMessage(e) })
      }
    }

    /** A compare as it is now: the agent's and git's sides again (a file made or deleted since), the other sides' texts. */
    const reloadCompare = async (id: string) => {
      const tab = tabsRef.current.find((t) => t.id === id)
      if (!tab || tab.kind !== 'diff') return
      try {
        if (tab.source && tab.source !== 'compare') update(id, await sidesOf(tab.path, tab.source))
        await loadSides(id)
        collect()
        // A side that became a file or stopped being one (made, deleted) has another model now: the compare shows it.
        const now = tabsRef.current.find((t) => t.id === id)
        if (now && activeRef.current === id && now.status === 'ready') present(now)
      } catch (e) {
        // The agent's change accepted or reverted elsewhere: the compare has nothing to show.
        if (tab.source === 'agent' && e instanceof ApiError && e.http === 404) drop([id])
      }
    }

    /** A Markdown file's preview: a tab of its own beside the file's, its model kept while either is open. */
    const openPreview = async (path: string) => {
      const id = `preview:${path}`
      if (has(id)) {
        activate(id)
        return
      }
      add({ id, kind: 'preview', path, status: 'ready', dirty: false })
      await loadFile(path).catch(() => undefined)
    }

    const modelFor = async (path: string) => {
      const f = await loadFile(path).catch(() => null)
      return f && typeof f !== 'string' ? f.model : null
    }

    /** The Settings or the Keyboard Shortcuts editor: one tab each, shown again when open. */
    const openPage = (kind: 'settings' | 'keybindings', query = '') => {
      setPageQuery(query)
      if (has(kind)) {
        activate(kind)
        return
      }
      add({ id: kind, kind, path: kind === 'settings' ? 'Settings' : 'Keyboard Shortcuts', status: 'ready', dirty: false })
    }

    /**
     * One change, or the changes the selection touches, of the compare shown: staged (the index takes them from the file),
     * unstaged (the index puts them back as HEAD has them), reverted (the file puts them back, as one edit Undo takes back)
     * or accepted (the agent's: what was before it takes them, and the rest stays to review).
     */
    const changeAction = async (kind: 'stage' | 'unstage' | 'revert' | 'accept') => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      const de = diffEditor.current
      const models = tab?.kind === 'diff' ? modelsOf(tab) : null
      if (!tab || !de || !models) return
      const original = models.original.getValue()
      const modified = models.modified.getValue()
      const sel = de.getModifiedEditor().getSelection?.()
      const at = sel ?? { startLineNumber: 1, endLineNumber: models.modified.getLineCount() }
      const picked = touched((de.getLineChanges() ?? []) as LineChange[], at.startLineNumber, at.endLineNumber)
      if (picked.length === 0) {
        toast.info('Put the cursor in a change, or select the lines of the changes.')
        return
      }
      try {
        if (kind === 'stage' && tab.source === 'git') queryClient.setQueryData(gitStatusQuery.queryKey, await gitStageText(tab.path, takeChanges(original, modified, picked)))
        else if (kind === 'unstage' && tab.source === 'staged') queryClient.setQueryData(gitStatusQuery.queryKey, await gitStageText(tab.path, revertChanges(original, modified, picked)))
        else if (kind === 'accept' && tab.source === 'agent') queryClient.setQueryData(changesQuery.queryKey, await acceptChange(tab.path, takeChanges(original, modified, picked)))
        else if (kind === 'revert' && tab.right?.kind === 'file') {
          const model = models.modified
          const edit = minimalEdit(modified, revertChanges(original, modified, picked))
          if (edit) {
            const a = model.getPositionAt(edit.start)
            const b = model.getPositionAt(edit.end)
            model.pushStackElement()
            model.pushEditOperations([], [{ range: { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column }, text: edit.text }], () => null)
            model.pushStackElement()
          }
          return
        } else return
        await reloadCompare(tab.id)
      } catch (e) {
        toast.error(errorMessage(e))
      }
    }

    /** Swaps a compare's sides (the agent's and git's become plain compares). */
    const swapSides = (id: string) => {
      const tab = tabsRef.current.find((t) => t.id === id)
      if (!tab?.left || !tab.right) return
      const next = { ...tab, source: 'compare' as const, left: tab.right, right: tab.left }
      const nid = idOf(next)
      if (has(nid)) {
        activate(nid)
        return
      }
      const own = diffs.current.get(id)
      diffs.current.delete(id)
      if (own) diffs.current.set(nid, { left: own.right, right: own.left })
      commit(tabsRef.current.map((t) => (t.id === id ? { ...next, id: nid } : t)))
      if (activeRef.current === id) activate(nid)
    }

    /**
     * Saves a file: first the save's steps for its language (trim trailing whitespace, format, the final newline…) as
     * edits Undo takes back; not for a save "afterDelay" makes as the person types. A file changed on disk asks before it
     * is written over (an auto save waits instead, and says so).
     */
    async function save(id?: string, reason: SaveReason = 'explicit'): Promise<boolean> {
      const tab = tabsRef.current.find((t) => t.id === (id ?? activeRef.current))
      let path: string | undefined
      if (tab?.kind === 'file') path = tab.path
      else if (tab) {
        // A compare: the side with the focus, else its right side's file, else its left's.
        const l = tab.left?.kind === 'file' ? tab.left.path : undefined
        const r = tab.right?.kind === 'file' ? tab.right.path : undefined
        const leftFocused = activeRef.current === tab.id && !!diffEditor.current?.getOriginalEditor?.().hasTextFocus?.()
        path = leftFocused && l ? l : (r ?? l)
      }
      const f = path ? files.current.get(path) : undefined
      if (!f || f.model.isDisposed()) return false
      return saveFile(f, reason)
    }

    async function saveFile(f: FileModel, reason: SaveReason): Promise<boolean> {
      if (reason !== 'auto') {
        const m = mon.current
        const v = settingsRef.current.all(f.model.getLanguageId?.())
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
      languageServers.saved(f.model)
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
      // The files with unsaved changes no other tab holds (another keeps what is unsaved in them): both sides of a compare may be.
      const unsavedFiles = fileSidesOf(tab).filter((p) => fileDirty(p) && !tabsRef.current.some((t) => t.id !== id && fileSidesOf(t).includes(p)))
      if (unsavedFiles.length === 0) {
        drop([id])
        return true
      }
      activate(id)
      const answer = await new Promise<Unsaved>((resolve) => setUnsaved({ paths: unsavedFiles, answer: resolve }))
      setUnsaved(null)
      if (answer === 'cancel') return false
      if (answer === 'save') {
        for (const p of unsavedFiles) {
          const f = files.current.get(p)
          if (f && !f.model.isDisposed() && !(await saveFile(f, 'explicit'))) return false
        }
      }
      drop([id], answer === 'discard' ? unsavedFiles : [])
      return true
    }

    /** The file as it was when a read began: still held, the same model, nothing typed in it and nothing saved since. */
    const unchanged = (f: FileModel, at: { alt: number; version: string }) =>
      files.current.get(f.path) === f && !f.model.isDisposed() && f.model.getAlternativeVersionId() === at.alt && f.version === at.version

    /** One file against the disk. Everything is checked again after each wait: it may have closed, been typed in or saved, meanwhile. */
    const refreshFile = async (f: FileModel) => {
      if (f.model.isDisposed() || f.model.getAlternativeVersionId() !== f.savedAlt) return
      const at = { alt: f.savedAlt, version: f.version }
      let file
      try {
        file = await readAny(f.path)
      } catch (e) {
        // Gone (a change reverted, a file deleted by a command): its tab with nothing unsaved goes too, and the compares of it
        // have nothing on that side.
        if (e instanceof ApiError && e.http === 404 && unchanged(f, at)) {
          drop(tabsRef.current.filter((t) => t.kind === 'file' && t.path === f.path).map((t) => t.id))
          for (const t of tabsRef.current.filter((x) => x.kind === 'diff' && fileSidesOf(x).includes(f.path))) {
            if (t.source && t.source !== 'compare') void reloadCompare(t.id)
            else drop([t.id])
          }
        }
        return
      }
      // Typed in while it was read: the person's text stays, unsaved, and saving asks first (the version on disk moved on).
      // Saved while it was read: the read may be from before the save, and what was saved stays.
      if (!unchanged(f, at) || file.version === f.version || file.text === null) return
      const edit = lineEdit(f.model.getValue(), file.text)
      if (edit) {
        // An edit of the lines that changed, not a new model: the cursor, the scroll and the breakpoints stay with their
        // lines (moved down by the lines added above them), and Undo takes it back.
        const a = f.model.getPositionAt(edit.start)
        const b = f.model.getPositionAt(edit.end)
        f.model.pushEditOperations([], [{ range: { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column }, text: edit.text }], () => null)
        f.model.pushStackElement()
      }
      f.version = file.version
      f.savedAlt = f.model.getAlternativeVersionId()
      markDirty(f)
    }

    /** Files changed on disk: the files the editor holds, then the compares of them (their other sides read again). */
    const refresh = async (path?: string) => {
      // A picture, sound or video changed on disk: fetched again.
      for (const t of tabsRef.current.filter((x) => x.kind === 'media' && (path === undefined || x.path === path))) update(t.id, { mediaVersion: (t.mediaVersion ?? 0) + 1 })
      for (const f of [...files.current.values()].filter((x) => path === undefined || x.path === path)) {
        try {
          await refreshFile(f)
        } catch {
          // One file that cannot be read again leaves the others to be.
        }
      }
      for (const t of tabsRef.current.filter((x) => x.kind === 'diff' && x.status === 'ready' && (path === undefined || [x.left, x.right].some((side) => side && sidePath(side) === path)))) {
        await reloadCompare(t.id)
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
      const ids = new Map<string, string>()
      commit(
        tabsRef.current.map((t) => {
          // The workbench's own pages (Settings, Keyboard Shortcuts) are no files, whatever their names.
          if (t.kind !== 'file' && t.kind !== 'diff') return t
          const touched = within(t.path, from) || [t.left, t.right].some((side) => side && sidePath(side) !== undefined && within(sidePath(side)!, from))
          if (!touched) return t
          const path = rename(t.path)
          const next: Tab = { ...t, path, left: t.left && renamed(t.left, rename), right: t.right && renamed(t.right, rename) }
          const id = t.kind === 'file' ? path : idOf(next)
          ids.set(t.id, id)
          const d = diffs.current.get(t.id)
          if (d && id !== t.id) {
            diffs.current.delete(t.id)
            diffs.current.set(id, d)
          }
          const language = m && t.status === 'ready' ? m.languageOf(path, settingsRef.current.get('files.associations')) : null
          return { ...next, id, language: language?.name ?? t.language, languageId: language?.id ?? t.languageId }
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
      unshow(old)
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

    /**
     * The lines chosen in the file shown (the cursor's when none are); with their text only when asked (it copies them). In a
     * compare whose "now" side is the file on disk (a change of the agent's, or one not committed), those of that side.
     */
    const selection = (withText = true): Piece | null => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      const right = tab?.kind === 'diff' && tab.status === 'ready' && tab.right?.kind === 'file' ? tab.right.path : null
      const ed = right ? diffEditor.current?.getModifiedEditor?.() : editor.current
      const path = right ?? (tab?.kind === 'file' ? tab.path : null)
      const model = ed?.getModel()
      if (!ed || !model || !path || files.current.get(path)?.model !== model) return null
      const s = ed.getSelection()
      const start = s?.startLineNumber ?? ed.getPosition()?.lineNumber ?? 1
      // A selection that ends at a line's start leaves that line out, as an editor's does.
      const end = s && s.endLineNumber > s.startLineNumber && s.endColumn === 1 ? s.endLineNumber - 1 : (s?.endLineNumber ?? start)
      return withText ? pieceOf(path, start, end) : { path, startLine: start, endLine: end }
    }

    const shownEditor = () => {
      const tab = tabsRef.current.find((t) => t.id === activeRef.current)
      return tab?.kind === 'file' && tab.status === 'ready' ? editor.current : null
    }
    const editorCommands = () =>
      (shownEditor()?.getSupportedActions() ?? []).filter((a) => a.label).map((a) => ({ id: actionId(a.id), label: a.label }))
    const runAction = (id: string) => {
      const ed = shownEditor()
      if (!ed) return
      ed.focus()
      void ed.getAction(actionId(id))?.run()
    }

    // A tab with unsaved changes stays: its text is the person's, and saving makes the file again.
    const removed = (path: string) => drop(tabsRef.current.filter((t) => (t.kind === 'file' || t.kind === 'diff') && within(t.path, path) && !t.dirty).map((t) => t.id))

    const accept = async (path?: string) => {
      try {
        queryClient.setQueryData(changesQuery.queryKey, await acceptChange(path))
        drop(tabsRef.current.filter((t) => t.kind === 'diff' && t.source === 'agent' && (path === undefined || t.path === path)).map((t) => t.id))
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
        drop(tabsRef.current.filter((t) => t.kind === 'diff' && t.source === 'agent' && t.path === path).map((t) => t.id))
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

    /** The folder's files a language server's edit changes: opened behind the tab shown, so the edit can be made in them. */
    const keep = async (paths: string[]) => {
      await Promise.all(paths.map((p) => open(p, undefined, true)))
    }

    return {
      open: (path: string, at?: Reveal) => open(path, at), keep, openDiff, activate: (id: string) => activate(id), close, save: (id?: string) => save(id), refresh, moved, removed, accept, revert, selection, pieceOf, editorCommands, runAction, showProblems, bindEditor, bindDiff,
      saveAll, closeMany, reopenClosed, cycle, activateIndex, navigate, focusEditor, toggleWordWrap, languages, setLanguage, activeModel, autoSaveOn,
      saveWith: save,
      compare,
      changeAction,
      openPage,
      openPreview,
      modelFor,
      swapSides,
      diffEditor: () => diffEditor.current,
      modelsOf,
      present,
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
    for (const f of files.current.values()) if (!f.model.isDisposed()) indentModel(f.model, settings.all(f.model.getLanguageId?.()), false)
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
        bindEditorContext(ed)
        installDebugEditor(m.monaco, ed, (model) => m.pathOf(model.uri))
        const decorations = settingsRef.current.get<DiffDecorations>('scm.diffDecorations')
        if (decorations !== 'none') {
          const dirty = installDirtyDiff(m.monaco, ed, (model) => m.pathOf(model.uri), decorations)
          // git's index moves on a save, a stage, a commit, a discard: the bars are drawn against it again.
          queryClient.getQueryCache().subscribe((e) => {
            if (e.type === 'updated' && e.query.queryKey[1] === 'git' && e.query.queryKey[2] === 'status' && e.action.type === 'success') dirty.refresh()
          })
        }
        addDebugActions(m.monaco, ed, (model) => m.pathOf(model.uri))
        // The actions added here are Monaco commands under the editor's own id ("vs.editor.ICodeEditor:1:codeArena.addToChat"):
        // keys and keybindings.json name them by their own (codeArena.addToChat, editor.debug.action.runToCursor).
        for (const a of ed.getSupportedActions()) {
          const own = actionId(a.id)
          if (own !== a.id) m.monaco.editor.registerCommand(own, () => void ed.getAction(own)?.run())
        }
        keymap.setEditorCommands(ed.getSupportedActions().map((a) => ({ id: actionId(a.id), label: a.label })))
        keymap.setEditorDefaults(monacoDefaults(ed))
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
    } else if (tab.kind === 'diff') {
      const d = actions.modelsOf(tab)
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
          if (side) bindEditorContext(side)
        }
        // The compare's "now" side asks the agent as the editor does: Add to chat, Explain, Fix, Complete (its right-click menu).
        const now = diffEditor.current.getModifiedEditor?.()
        if (now) askAboutCode(m, now, actions.selection)
        // The compare's toolbar reads the diff editor: it renders again now that there is one.
        setDiffMade(true)
      }
      actions.present(tab)
      setCursor(null)
    }
  }, [shown, revealed, actions, queryClient])

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
        d.left?.dispose()
        d.right?.dispose()
      }
    }
  }, [])

  const value = useMemo<EditorApi>(
    () => ({ ...actions, tabs, active, cursor, quickOpen, setQuickOpen, quickText, setQuickText, pageQuery, diffMade }),
    [actions, tabs, active, cursor, quickOpen, setQuickOpen, quickText, pageQuery, diffMade],
  )
  return (
    <EditorContext value={value}>
      {children}
      <UnsavedDialog paths={unsaved?.paths ?? null} onAnswer={(a) => unsaved?.answer(a)} />
      <QuickOpen />
    </EditorContext>
  )
}

/** Closing a file (or a compare of files) with unsaved changes: Save, Don't save, or Cancel. */
function UnsavedDialog({ paths, onAnswer }: { paths: string[] | null; onAnswer: (a: Unsaved) => void }) {
  const names = (paths ?? []).map(nameOf)
  return (
    <Dialog open={paths !== null} onOpenChange={(open) => !open && onAnswer('cancel')}>
      <DialogContent hideClose className="max-w-md">
        <DialogHeader>
          <DialogTitle>Save the changes to {names.length > 1 ? `${names.slice(0, -1).join(', ')} and ${names.at(-1)}` : names[0]}?</DialogTitle>
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
/** What quick open does by the first character typed, as VS Code's: files, > commands, @ this file's symbols, # the folder's, ? the list. */
const quickModes = [
  { prefix: '', label: 'Go to a file', hint: 'a few letters of its path; app.ts:12 for a line' },
  { prefix: '>', label: 'Run a command', hint: 'the Command Palette' },
  { prefix: '@', label: 'Go to a symbol in this file', hint: "its name, or its parts' initials" },
  { prefix: '#', label: 'Go to a symbol in the folder', hint: "its name, or its parts' initials (rta: RunToolsAsync)" },
  { prefix: ':', label: 'Go to a line in this file', hint: ':12, or :12:5 for a column' },
  { prefix: '?', label: 'List what quick open does', hint: '' },
] as const

function QuickOpen() {
  const { quickOpen, setQuickOpen, open, tabs, active, quickText: typed, setQuickText: setTyped } = useEditor()
  const mode = typed.startsWith('#') ? 'folder' : typed.startsWith('@') ? 'file' : typed.startsWith('?') ? 'help' : 'files'
  const all = useQuery({ ...allFilesQuery, enabled: quickOpen && mode === 'files', staleTime: 10_000 })
  // The symbols asked for once typing pauses: one request as a name is typed, not one a letter.
  const symbolText = mode === 'folder' || mode === 'file' ? typed.slice(1).trim() : ''
  const [asked, setAsked] = useState(symbolText)
  useEffect(() => {
    const wait = setTimeout(() => setAsked(symbolText), 150)
    return () => clearTimeout(wait)
  }, [symbolText])
  const filePath = active?.kind === 'file' ? active.path : null
  const symbols = useQuery({
    ...symbolsQuery(asked, mode === 'file' ? (filePath ?? undefined) : undefined),
    enabled: quickOpen && (mode === 'file' ? !!filePath : mode === 'folder' && asked.length > 0),
    staleTime: 2_000,
  })
  // > hands what follows to the Command Palette.
  useEffect(() => {
    if (!quickOpen || !typed.startsWith('>')) return
    setQuickOpen(false)
    setTyped('')
    void runCommand('workbench.action.showCommands')
  }, [quickOpen, typed, setQuickOpen, setTyped])
  const close = () => {
    setQuickOpen(false)
    setTyped('')
  }
  const recent = [...tabs].reverse().filter((t) => t.kind === 'file').map((t) => t.path)
  // :12 goes to line 12 of the file shown; app.ts:12 opens the file found at line 12.
  const at = /^(.*?):(\d+)(?::(\d+))?$/.exec(typed.trim())
  const name = at ? at[1]! : typed
  const line = at ? Number(at[2]) : undefined
  const column = at?.[3] ? Number(at[3]) : undefined
  const here = at && !name.trim() && active?.kind === 'file' ? active.path : null
  const shown = mode !== 'files' ? [] : here ? [here] : name.trim() ? rankFiles(all.data?.files ?? [], name) : [...new Set([...recent, ...(all.data?.files ?? [])])].slice(0, 50)
  const titles = {
    files: ['Go to file', "Type a few letters of a file's path, and :line to go to a line; ? lists what else it does", 'Go to file: a few letters of its path (:12 for a line)'],
    file: ['Go to symbol in this file', "Type a few letters of a symbol's name", "Go to a symbol in this file: @ and a few letters of its name"],
    folder: ['Go to symbol in the folder', "Type a symbol's name, or its parts' initials", 'Go to a symbol in the folder: # and its name, or its initials'],
    help: ['What quick open does', 'Choose a mode', 'What quick open does'],
  } as const
  const [title, description, placeholder] = titles[mode]
  const hits = symbols.data?.symbols ?? []
  return (
    <CommandDialog
      open={quickOpen}
      onOpenChange={(o) => {
        setQuickOpen(o)
        if (!o) setTyped('')
      }}
      title={title}
      description={description}
      shouldFilter={false}
    >
      <CommandInput value={typed} onValueChange={setTyped} placeholder={placeholder} aria-label="File to open" />
      <CommandList>
        {mode === 'files' && (
          <>
            {all.isPending && <p className="py-6 text-center text-sm text-muted-foreground">Listing the files…</p>}
            {all.error && <p className="py-6 text-center text-sm text-muted-foreground">{errorMessage(all.error)}</p>}
            {all.data && <CommandEmpty>No file matches.</CommandEmpty>}
            {shown.map((path) => (
              <CommandItem
                key={path}
                value={path}
                onSelect={() => {
                  close()
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
          </>
        )}
        {(mode === 'file' || mode === 'folder') && (
          <>
            {mode === 'file' && !filePath && <p className="py-6 text-center text-sm text-muted-foreground">Open a file to list its symbols.</p>}
            {mode === 'folder' && !symbolText && <p className="py-6 text-center text-sm text-muted-foreground">Type a symbol&apos;s name: a few letters, or its parts&apos; initials (rta for RunToolsAsync).</p>}
            {symbols.data?.building && <p className="py-6 text-center text-sm text-muted-foreground">The code index is being made for this folder: ask again in a moment.</p>}
            {symbols.error && <p className="py-6 text-center text-sm text-muted-foreground">{errorMessage(symbols.error)}</p>}
            {symbols.data && !symbols.data.building && hits.length === 0 && <p className="py-6 text-center text-sm text-muted-foreground">No symbol matches.</p>}
            {hits.map((s) => (
              <CommandItem
                key={`${s.path}:${s.line}:${s.name}`}
                value={`${s.path}:${s.line}:${s.name}`}
                onSelect={() => {
                  close()
                  void open(s.path, { line: s.line })
                }}
              >
                <FileCode2 aria-hidden="true" />
                <span className="truncate font-medium">{s.name}</span>
                <span className="text-xs text-muted-foreground">{s.kind}</span>
                <span className="min-w-0 truncate text-xs text-muted-foreground">
                  {s.container ? `${s.container} · ` : ''}
                  {mode === 'folder' ? `${s.path}:${s.line}` : `:${s.line}`}
                </span>
              </CommandItem>
            ))}
          </>
        )}
        {mode === 'help' &&
          quickModes.map((m) => (
            <CommandItem key={m.prefix || 'files'} value={m.label} onSelect={() => setTyped(m.prefix)}>
              <span className="w-4 font-mono text-muted-foreground">{m.prefix || '…'}</span>
              <span className="truncate font-medium">{m.label}</span>
              <span className="min-w-0 truncate text-xs text-muted-foreground">{m.hint}</span>
            </CommandItem>
          ))}
      </CommandList>
    </CommandDialog>
  )
}

/** The tabs, the editor or the diff of the active one, and a few words when nothing is open. */
export function EditorArea() {
  const ed = useEditor()
  const { tabs, active, bindEditor, bindDiff } = ed
  const changes = useQuery(changesQuery)
  const change = active?.kind === 'diff' && active.source === 'agent' ? changes.data?.find((c) => c.path === active.path) : undefined
  const showsFile = active?.kind === 'file' && active.status === 'ready'
  const showsDiff = active?.kind === 'diff' && active.status === 'ready'
  return (
    <section aria-label="Editor" className="flex min-h-0 min-w-0 flex-1 flex-col bg-background">
      {tabs.length > 0 && (
        <div role="tablist" aria-label="Open files" className="flex h-9 shrink-0 overflow-x-auto overflow-y-hidden border-b bg-sidebar [scrollbar-width:thin]">
          {tabs.map((t) => {
            const selected = t.id === active?.id
            const name = isUserFile(t.path) ? t.path.slice(5) : t.source === 'compare' && t.left && t.right ? `${sideLabel(t.left)} ↔ ${sideLabel(t.right)}` : nameOf(t.path)
            return (
              <ContextMenu.Root key={t.id}>
                <ContextMenu.Trigger asChild>
              <div
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
                  title={t.kind === 'diff' ? (t.source === 'compare' ? name : `${t.path}: ${t.source === 'git' ? 'its changes, not staged' : t.source === 'staged' ? 'its staged changes' : "the agent's changes"}`) : isUserFile(t.path) ? `Your ${t.path.slice(5)} (every project)` : t.path}
                  onClick={() => ed.activate(t.id)}
                  onAuxClick={(e) => e.button === 1 && void ed.close(t.id)}
                  className="flex h-full max-w-56 items-center gap-1.5 pr-1 pl-3 outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset"
                >
                  {t.kind === 'diff' ? (
                    <GitCompareArrows className="size-3.5 shrink-0 text-primary" aria-hidden="true" />
                  ) : t.kind === 'settings' ? (
                    <Settings2 className="size-3.5 shrink-0" aria-hidden="true" />
                  ) : t.kind === 'keybindings' ? (
                    <Keyboard className="size-3.5 shrink-0" aria-hidden="true" />
                  ) : t.kind === 'media' ? (
                    <FileImage className="size-3.5 shrink-0" aria-hidden="true" />
                  ) : t.kind === 'preview' ? (
                    <Eye className="size-3.5 shrink-0" aria-hidden="true" />
                  ) : (
                    <FileCode2 className="size-3.5 shrink-0" aria-hidden="true" />
                  )}
                  <span className="truncate">{name}</span>
                  {isUserFile(t.path) && <span className="text-xs text-muted-foreground">User</span>}
                  {t.kind === 'diff' && t.source !== 'compare' && <span className="text-xs text-muted-foreground">{t.source === 'git' ? 'working tree' : t.source === 'staged' ? 'index' : 'changes'}</span>}
                  {t.dirty && <span className="sr-only">, not saved</span>}
                </button>
                <button
                  type="button"
                  aria-label={`Close ${name}${t.kind === 'diff' && t.source !== 'compare' ? ' changes' : ''}`}
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
                </ContextMenu.Trigger>
                <ContextMenu.Portal>
                  <TabMenu tab={t} />
                </ContextMenu.Portal>
              </ContextMenu.Root>
            )
          })}
        </div>
      )}
      {active?.kind === 'file' && (
        <div className="flex h-8 shrink-0 items-center gap-2 border-b px-3 text-xs text-muted-foreground">
          <span className="min-w-0 truncate font-mono" title={active.path}>
            {isUserFile(active.path) ? `User › ${active.path.slice(5)}` : active.path.split('/').join(' › ')}
          </span>
          {active.languageId === 'markdown' && (
            <button type="button" onClick={() => void ed.openPreview(active.path)} className="ml-auto flex items-center gap-1 rounded px-1.5 py-0.5 hover:bg-accent hover:text-foreground" title="Open Preview (Ctrl+Shift+V)">
              <Eye className="size-3.5" aria-hidden="true" />
              Preview
            </button>
          )}
        </div>
      )}
      {active?.kind === 'diff' && <CompareToolbar tab={active} change={change} />}
      <div id="editor-panel" role={active ? 'tabpanel' : undefined} aria-label={active ? nameOf(active.path) : undefined} className="relative min-h-0 flex-1">
        {/* Not shown: display none (Monaco's parts set their own visibility); shown again, it lays itself out anew. */}
        {/* monaco-component: Monaco's colours (--vscode-*) for what it shows beside the editor, not in it: its right-click
            menu (fixedOverflowWidgets) sits in this box, and without them it had no background. */}
        <div ref={bindEditor} data-testid="code-editor" hidden={!showsFile} className="monaco-component absolute inset-0" />
        <div ref={bindDiff} data-testid="diff-editor" hidden={!showsDiff} className="monaco-component absolute inset-0" />
        {active?.kind === 'settings' && (
          <Suspense fallback={<Spinner label="Opening the settings" />}>
            <SettingsEditor />
          </Suspense>
        )}
        {active?.kind === 'keybindings' && (
          <Suspense fallback={<Spinner label="Opening the keyboard shortcuts" />}>
            <KeybindingsEditor />
          </Suspense>
        )}
        {active?.kind === 'media' && <MediaPreview key={active.id} tab={active} />}
        {active?.kind === 'preview' && <MarkdownPreview key={active.id} path={active.path} />}
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

/**
 * A Markdown file as it reads, following its text as it is edited (150 ms after typing pauses). A link to another file
 * opens it in the editor, a #heading scrolls to it, a web address opens a tab of the browser; a picture beside the file
 * shows from the folder.
 */
function MarkdownPreview({ path }: { path: string }) {
  const ed = useEditor()
  const [text, setText] = useState<string | null>(null)
  const box = useRef<HTMLDivElement>(null)
  useEffect(() => {
    let gone = false
    let wait = 0
    let listen: { dispose(): void } | null = null
    void ed.modelFor(path).then((model) => {
      if (gone) return
      if (!model) {
        setText('')
        return
      }
      setText(model.getValue())
      listen = model.onDidChangeContent(() => {
        clearTimeout(wait)
        wait = window.setTimeout(() => setText(model.getValue()), 150)
      })
    })
    return () => {
      gone = true
      clearTimeout(wait)
      listen?.dispose()
    }
  }, [ed, path])
  const dir = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : ''
  // Pictures named beside the file come from the folder (an answer's never do: the sanitizer leaves their words).
  const shown = useMemo(
    () =>
      text?.replace(/(!\[[^\]]*\]\()([^)\s]+)((?:\s+"[^"]*")?\))/g, (all, open: string, src: string, close: string) =>
        /^(?:[a-z][a-z0-9+.-]*:|\/|#)/i.test(src) ? all : `${open}${rawFileUrl(joinPath(dir, decodeURI(src)))}${close}`,
      ) ?? null,
    [text, dir],
  )
  // Its links (clicked, or Enter on one): a #heading scrolls to it, another file opens in the editor.
  useEffect(() => {
    const el = box.current
    if (!el) return
    const click = (e: MouseEvent) => {
      const a = (e.target as HTMLElement).closest('a')
      const href = a?.getAttribute('href')
      if (!a || !href || /^[a-z][a-z0-9+.-]*:/i.test(href)) return
      e.preventDefault()
      if (href.startsWith('#')) {
        const id = decodeURIComponent(href.slice(1))
        const heading = [...el.querySelectorAll('h1, h2, h3, h4, h5, h6')].find((h) => h.id === id || slug(h.textContent ?? '') === id)
        heading?.scrollIntoView({ block: 'start' })
        return
      }
      const file = href.split('#')[0]
      if (file) void ed.open(joinPath(dir, decodeURI(file)))
    }
    el.addEventListener('click', click)
    return () => el.removeEventListener('click', click)
  }, [ed, dir, text])
  if (text === null) return <Spinner label={`Opening ${nameOf(path)}`} />
  return (
    <div ref={box} className="absolute inset-0 overflow-auto bg-background px-8 py-6" aria-label={`Preview ${nameOf(path)}`} role="document">
      <article className="mx-auto max-w-3xl">
        <Markdown text={shown ?? ''} />
      </article>
    </div>
  )
}

/** A heading's anchor as GitHub and VS Code make it: lower case, spaces as -, punctuation gone. */
const slug = (text: string) => text.trim().toLowerCase().replace(/[^\p{L}\p{N}\s-]/gu, '').replace(/\s/g, '-')

/** A path relative to a folder of the working directory, ./ and ../ worked out. */
function joinPath(dir: string, relative: string): string {
  const parts = relative.startsWith('/') ? [] : dir.split('/').filter(Boolean)
  for (const p of relative.split('/')) {
    if (p === '' || p === '.') continue
    if (p === '..') parts.pop()
    else parts.push(p)
  }
  return parts.join('/')
}

/**
 * A picture, sound or video in a tab of its own, as VS Code's preview shows one: a picture centred on a checkerboard, its
 * size said, a click between fitting the tab and its own size; sound and video with their controls.
 */
function MediaPreview({ tab }: { tab: Tab }) {
  const [whole, setWhole] = useState(false)
  const [size, setSize] = useState<{ width: number; height: number } | null>(null)
  const kind = mediaKind(tab.path)
  const src = `${rawFileUrl(tab.path)}&v=${tab.mediaVersion ?? 0}`
  const name = nameOf(tab.path)
  if (kind === 'sound' || kind === 'video') {
    return (
      <div className="absolute inset-0 grid place-items-center overflow-auto bg-background p-6">
        {kind === 'sound' ? (
          <audio controls src={src} aria-label={name} className="w-full max-w-xl">
            <track kind="captions" />
          </audio>
        ) : (
          <video controls src={src} aria-label={name} className="max-h-full max-w-full">
            <track kind="captions" />
          </video>
        )}
      </div>
    )
  }
  return (
    <div
      className="absolute inset-0 grid place-items-center overflow-auto p-6"
      style={{ backgroundImage: 'repeating-conic-gradient(var(--muted) 0% 25%, transparent 0% 50%)', backgroundSize: '16px 16px' }}
    >
      <button type="button" onClick={() => setWhole((w) => !w)} title={whole ? 'Fit the tab' : 'Its own size'} className="contents">
        <img
          src={src}
          alt={name}
          onLoad={(e) => setSize({ width: e.currentTarget.naturalWidth, height: e.currentTarget.naturalHeight })}
          className={whole ? 'max-w-none cursor-zoom-out' : 'max-h-full max-w-full cursor-zoom-in object-contain'}
        />
      </button>
      {size && (
        <p className="absolute right-2 bottom-2 rounded bg-background/80 px-2 py-0.5 text-xs text-muted-foreground">
          {size.width} × {size.height}
        </p>
      )}
    </div>
  )
}

/** A tab's right-click menu, as VS Code's: closing it and others, its path, compares. */
function TabMenu({ tab }: { tab: Tab }) {
  const ed = useEditor()
  const run = (command: string) => {
    ed.activate(tab.id)
    requestAnimationFrame(() => void runCommand(command))
  }
  const at = ed.tabs.findIndex((t) => t.id === tab.id)
  const keys = {
    close: useKeyFor('workbench.action.closeActiveEditor'),
    saved: useKeyFor('workbench.action.closeUnmodifiedEditors'),
    all: useKeyFor('workbench.action.closeAllEditors'),
    withSaved: useKeyFor('workbench.files.action.compareWithSaved'),
    withClipboard: useKeyFor('workbench.files.action.compareWithClipboard'),
  }
  const item = (label: string, onSelect: () => void, keys?: string) => (
    <ContextMenu.Item className={menuItem} onSelect={onSelect}>
      {label}
      {keys && <span className="ml-auto pl-4 text-xs text-muted-foreground">{keys}</span>}
    </ContextMenu.Item>
  )
  const separator = <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
  return (
    <ContextMenu.Content className={menuContent}>
      {item('Close', () => void ed.close(tab.id), keys.close)}
      {item('Close Others', () => void ed.closeMany(ed.tabs.filter((t) => t.id !== tab.id).map((t) => t.id)))}
      {item('Close to the Right', () => void ed.closeMany(ed.tabs.slice(at + 1).map((t) => t.id)))}
      {item('Close Saved', () => void ed.closeMany(ed.tabs.filter((t) => !t.dirty).map((t) => t.id)), keys.saved)}
      {item('Close All', () => void ed.closeMany(ed.tabs.map((t) => t.id)), keys.all)}
      {!isUserFile(tab.path) && (
        <>
          {separator}
          {item('Copy Path', () => run('workbench.action.files.copyPathOfActiveFile'))}
          {item('Copy Relative Path', () => run('copyRelativeFilePath'))}
        </>
      )}
      {tab.kind === 'file' && (
        <>
          {separator}
          {item('Compare with Saved', () => run('workbench.files.action.compareWithSaved'), keys.withSaved)}
          {item('Compare with Clipboard', () => run('workbench.files.action.compareWithClipboard'), keys.withClipboard)}
          {item('Compare With…', () => run('workbench.files.action.compareFileWith'))}
          {item('Select for Compare', () => run('selectForCompare'))}
        </>
      )}
      {tab.kind === 'diff' && (
        <>
          {separator}
          {item('Swap Left and Right Side', () => ed.swapSides(tab.id))}
        </>
      )}
    </ContextMenu.Content>
  )
}

function Welcome() {
  const keys: [string | undefined, string][] = [
    [useKeyFor('workbench.action.files.openFolder'), 'Open a folder'],
    [useKeyFor('workbench.action.quickOpen'), 'Go to a file'],
    [useKeyFor('workbench.action.showCommands'), 'Show all commands'],
    [useKeyFor('workbench.action.findInFiles'), 'Search the files'],
    [useKeyFor('workbench.action.terminal.toggleTerminal'), 'Show or hide the terminal'],
    [useKeyFor('workbench.action.openSettings'), 'Settings'],
  ]
  return (
    <div className="@container absolute inset-0 grid place-items-center overflow-auto p-6">
      <div className="grid w-full max-w-sm min-w-0 animate-enter justify-items-center gap-4 text-center">
        <div className="grid size-14 place-items-center rounded-2xl border bg-card shadow-sm">
          <FileDiff className="size-6 text-primary" aria-hidden="true" />
        </div>
        <div>
          <h2 className="text-base font-semibold">No file is open</h2>
          <p className="mt-1 text-sm text-muted-foreground">Open one from the Explorer, or ask the agent in the chat: what it changes shows under Agent changes.</p>
        </div>
        <Button variant="outline" size="sm" onClick={() => runCommand('workbench.action.files.openFolder')}>
          <FolderOpen /> Open Folder…
        </Button>
        {/* The keys only where there is room for them beside what they do. */}
        <dl className="hidden w-full grid-cols-[auto_1fr] items-center gap-x-4 gap-y-2 text-left text-sm @xs:grid">
          {keys.filter((k): k is [string, string] => !!k[0]).map(([k, what]) => (
            <div key={k} className="contents">
              <dt className="justify-self-end">
                <Kbd>{k}</Kbd>
              </dt>
              <dd className="text-muted-foreground">{what}</dd>
            </div>
          ))}
        </dl>
        <p className="hidden text-xs text-muted-foreground @xs:block">The activity bar on the left switches between the Explorer, Search, Agent changes and Chat.</p>
      </div>
    </div>
  )
}
