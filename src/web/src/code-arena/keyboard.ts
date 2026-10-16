import { useQuery } from '@tanstack/react-query'
import { useEffect, useSyncExternalStore } from 'react'
import { getCommand, hasCommand, onCommandsChange, runCommand } from './commands'
import { defaultKeybindings } from './default-keybindings'
import { onExtensionKeybindings } from './extensions'
import { userFileQuery } from './ide-api'
import { parse } from './jsonc'
import { buildRules, chordLabel, chordOf, isMac, keyFor, keysLabel, parseKey, resolve, sameChord, type Chord, type Context, type Rule, type RuleJson, type RuleProblem } from './keys'
import { useSetting } from './settings'

/**
 * The page's keymap: VS Code's defaults with the person's keybindings.json over them, the context keys the when clauses
 * read, and the chord waiting for its second key. One store for the page: the workbench reads keys with it outside the
 * editor, and the editor gives Monaco the same rules.
 */
interface KeymapState {
  rules: Rule[]
  problems: RuleProblem[]
  /** The first key of a chord, waiting for the second. */
  pending: Chord | null
  /** What the status bar says about keys: a chord waiting, a chord that is no command. */
  status: string | null
  version: number
}

const state: KeymapState = { rules: buildRules(defaultKeybindings, []).rules, problems: [], pending: null, status: null, version: 0 }
const listeners = new Set<() => void>()
let extensionRules: RuleJson[] = []
let userRules: unknown[] = []
/** Monaco's own default keys, as rules: listed and taken away by keybindings.json, never given back to Monaco. */
let monacoRules: RuleJson[] = []
const context: Context = {}
/** The editor's own commands (Monaco's actions), by id with their titles: a key for one of them in keybindings.json is no mistake. */
const editorCommands = new Map<string, string>()

const changed = () => {
  state.version++
  for (const l of listeners) l()
}

const subscribe = (f: () => void) => {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

/** The rules as they are now (Monaco is given them too). */
export const keymap = {
  get rules() {
    return state.rules
  },
  get problems() {
    return state.problems
  },
  subscribe,
  /** The person's keybindings.json, read. */
  setUser(rules: unknown[]) {
    userRules = rules
    rebuild()
  },
  /** The rules extensions bring. */
  setExtensions(rules: RuleJson[]) {
    extensionRules = rules
    rebuild()
  },
  /** Monaco's own commands, once the editor is there. */
  setEditorCommands(list: { id: string; label: string }[]) {
    for (const c of list) editorCommands.set(c.id, c.label)
    rebuild()
  },
  /** Monaco's own default keys, once the editor is there. */
  setEditorDefaults(rules: RuleJson[]) {
    monacoRules = rules
    rebuild()
  },
  /** Monaco's own commands known so far: id to title. */
  get editorCommands(): ReadonlyMap<string, string> {
    return editorCommands
  },
}

function rebuild() {
  // An extension's key for a command Code Arena does not have is left out: in the editor it would swallow the key.
  const extension = extensionRules.filter((r) => hasCommand(r.command) || editorCommands.has(r.command))
  const built = buildRules(defaultKeybindings, userRules, (id) => hasCommand(id) || editorCommands.has(id) || id.startsWith('editor.'), extension, monacoRules)
  state.rules = built.rules
  state.problems = built.problems
  changed()
}

// The extensions' keys as they change, and again as commands come (an extension's key waits for its command).
let rebuildQueued = false
onCommandsChange(() => {
  if (extensionRules.length === 0 || rebuildQueued) return
  rebuildQueued = true
  queueMicrotask(() => {
    rebuildQueued = false
    rebuild()
  })
})
onExtensionKeybindings((rules) => {
  if (rules.length > 0 || extensionRules.length > 0) keymap.setExtensions(rules)
})

/**
 * The context keys Monaco's own resolver is given too (for the rules it applies in the editor): the workbench's that
 * Monaco does not set itself. editorIsOpen and textCompareEditorVisible are each editor's own.
 */
const editorKeys = ['inDebugMode', 'debugState', 'debugType', 'sideBarVisible', 'panelVisible', 'auxiliaryBarVisible', 'resourceLangId', 'resourceFilename', 'activeEditor', 'activeViewlet']
const editorContexts: Map<string, { set(v: unknown): void }>[] = []

/** An editor of Monaco's gets the workbench's context keys, kept up to date. */
export function bindEditorContext(ed: { createContextKey?: (key: string, value: never) => { set(v: never): void } }) {
  if (!ed.createContextKey) return
  const keys = new Map<string, { set(v: unknown): void }>()
  for (const k of editorKeys) keys.set(k, ed.createContextKey(k, (context[k] ?? false) as never) as { set(v: unknown): void })
  editorContexts.push(keys)
}

/** Sets context keys the when clauses read (sideBarVisible, editorIsOpen…). */
export function setContext(values: Context) {
  Object.assign(context, values)
  for (const keys of editorContexts) for (const [k, v] of Object.entries(values)) keys.get(k)?.set(v ?? false)
}

export const contextKeys = () => context

let quiet: ReturnType<typeof setTimeout> | undefined
function say(status: string | null) {
  state.status = status
  changed()
  // A chord that is no command is said for a few seconds, as VS Code does.
  clearTimeout(quiet)
  if (status && !state.pending) quiet = setTimeout(() => say(null), 4000)
}

/** A command's key now, as the system shows it ("Ctrl+Shift+P", "⇧⌘P"); undefined when none. */
export function useKeyFor(command: string): string | undefined {
  useSyncExternalStore(subscribe, () => state.version)
  return keyFor(state.rules, command)
}

/** The keymap's version: changes with keybindings.json and the chord's state. */
export function useKeymap() {
  useSyncExternalStore(subscribe, () => state.version)
  return state
}

/** The commands the terminal lets the workbench take (their keys never reach the shell), as VS Code's defaults. */
export const defaultSkipShell = [
  'workbench.action.showCommands', 'workbench.action.quickOpen', 'workbench.action.gotoLine', 'workbench.action.openSettings', 'workbench.action.openGlobalKeybindings',
  'workbench.action.toggleSidebarVisibility', 'workbench.action.togglePanel', 'workbench.action.toggleAuxiliaryBar', 'workbench.action.chat.open',
  'workbench.action.terminal.toggleTerminal', 'workbench.action.terminal.new', 'workbench.action.focusSideBar', 'workbench.action.focusFirstEditorGroup',
  'workbench.action.findInFiles', 'workbench.action.replaceInFiles', 'workbench.view.explorer', 'workbench.view.scm', 'workbench.view.debug', 'workbench.view.extensions',
  'workbench.actions.view.problems', 'workbench.action.nextEditor', 'workbench.action.previousEditor', 'workbench.action.quickOpenView',
  'workbench.action.debug.start', 'workbench.action.debug.run', 'workbench.action.debug.continue', 'workbench.action.debug.pause', 'workbench.action.debug.stop',
  'workbench.action.debug.restart', 'workbench.action.debug.stepOver', 'workbench.action.debug.stepInto', 'workbench.action.debug.stepOut',
  'workbench.action.tasks.build', 'workbench.action.tasks.runTask', 'workbench.action.tasks.test',
]

/** The browser's keys the page always keeps: reload (F5, Ctrl+F5, Shift+F5) and save (Ctrl+S, ⌘S). */
const guardedKeys = () => ['f5', 'shift+f5', 'ctrl+f5', 'ctrl+shift+f5', isMac() ? 'cmd+s' : 'ctrl+s'].map((k) => parseKey(k)![0]!)

const inputFocused = (el: Element | null) => !!el && (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement || (el as HTMLElement).isContentEditable)

/**
 * The workbench's keys, wherever the focus is but in the code editor (Monaco reads the same rules there): the last rule
 * that matches runs its command; a chord's first key waits for its second; in a terminal only the commands that skip the
 * shell (and chords, with "terminal.integrated.allowChords"). A key of a command the page does not have now goes on.
 */
export function useKeyboard() {
  const keybindings = useQuery(userFileQuery('keybindings.json'))
  const allowChords = useSetting<boolean>('terminal.integrated.allowChords')
  const sendAll = useSetting<boolean>('terminal.integrated.sendKeybindingsToShell')
  const extraSkip = useSetting<string[]>('terminal.integrated.commandsToSkipShell')

  useEffect(() => {
    const text = keybindings.data?.text
    if (text === undefined) return
    const { value, errors } = parse(text)
    keymap.setUser(errors.length === 0 && Array.isArray(value) ? value : [])
  }, [keybindings.data?.text])

  useEffect(() => {
    const skip = new Set(sendAll ? [] : defaultSkipShell)
    for (const c of extraSkip ?? []) {
      if (c.startsWith('-')) skip.delete(c.slice(1))
      else skip.add(c)
    }
    const onKey = (e: KeyboardEvent) => {
      const target = e.target instanceof Element ? e.target : null
      // Monaco's own resolver has the same rules inside the editor; a box that records keys takes them as they are.
      if (target?.closest('.monaco-editor, [data-keys-raw]')) {
        if (state.pending) {
          state.pending = null
          say(null)
        }
        return
      }
      const chord = chordOf(e)
      if (!chord) return
      const inTerminal = !!target?.closest('.xterm')
      const ctx: Context = {
        ...context,
        inputFocus: inputFocused(target),
        terminalFocus: inTerminal,
        filesExplorerFocus: !!target?.closest('[data-context="explorer"]'),
        editorTextFocus: false,
        isInDiffEditor: false,
      }
      // Monaco's own keys are its resolver's: they do nothing outside the editor.
      const accept = (r: Rule) => r.source !== 'editor' && (!inTerminal || skip.has(r.command) || (allowChords && r.chords.length === 2))
      const pending = state.pending
      const found = resolve(state.rules, chord, ctx, pending, accept)
      if (pending) {
        state.pending = null
        e.preventDefault()
        e.stopPropagation()
        if (found.kind === 'command' && runCommand(found.rule.command, found.rule.args)) say(null)
        else say(`The key combination (${chordLabel(pending)}, ${chordLabel(chord)}) is not a command.`)
        return
      }
      if (found.kind === 'chord') {
        state.pending = found.first
        say(`(${chordLabel(found.first)}) was pressed. Waiting for the second key of the chord…`)
        e.preventDefault()
        e.stopPropagation()
        return
      }
      if (found.kind === 'command' && getCommand(found.rule.command)) {
        // A command that is off now (Save on the Settings page) still keeps its key from the browser.
        e.preventDefault()
        if (!runCommand(found.rule.command, found.rule.args)) return
        e.stopPropagation()
        if (state.status) say(null)
      }
    }
    // After the editor and the terminals had the key (they take theirs): the browser's reload and save never run, whether
    // or not a rule of the debugger's holds now (F5 while a session runs, Shift+F5 after a run without debugging).
    const guard = (e: KeyboardEvent) => {
      if (e.defaultPrevented || (e.target instanceof Element && e.target.closest('.xterm'))) return
      const chord = chordOf(e)
      if (chord && guardedKeys().some((g) => sameChord(g, chord))) e.preventDefault()
    }
    window.addEventListener('keydown', onKey, true)
    window.addEventListener('keydown', guard)
    return () => {
      window.removeEventListener('keydown', onKey, true)
      window.removeEventListener('keydown', guard)
    }
  }, [allowChords, sendAll, extraSkip])
}

export { keysLabel }
