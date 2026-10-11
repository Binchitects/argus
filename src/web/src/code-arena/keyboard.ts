import { useQuery } from '@tanstack/react-query'
import { useEffect, useSyncExternalStore } from 'react'
import { getCommand, hasCommand, runCommand } from './commands'
import { defaultKeybindings } from './default-keybindings'
import { userFileQuery } from './ide-api'
import { parse } from './jsonc'
import { buildRules, chordLabel, chordOf, keyFor, keysLabel, resolve, type Chord, type Context, type Rule, type RuleJson, type RuleProblem } from './keys'
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
const context: Context = {}
/** The editor's own commands (Monaco's actions): a key for one of them in keybindings.json is no mistake. */
const editorCommands = new Set<string>()

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
  setEditorCommands(ids: string[]) {
    for (const id of ids) editorCommands.add(id)
    rebuild()
  },
}

function rebuild() {
  const built = buildRules(defaultKeybindings, userRules, (id) => hasCommand(id) || editorCommands.has(id) || id.startsWith('editor.'), extensionRules)
  state.rules = built.rules
  state.problems = built.problems
  changed()
}

/** Sets context keys the when clauses read (sideBarVisible, editorIsOpen…). */
export function setContext(values: Context) {
  Object.assign(context, values)
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
      const accept = inTerminal ? (r: Rule) => skip.has(r.command) || (allowChords && r.chords.length === 2) : undefined
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
      if (found.kind === 'command' && getCommand(found.rule.command) && runCommand(found.rule.command, found.rule.args)) {
        e.preventDefault()
        e.stopPropagation()
        if (state.status) say(null)
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [allowChords, sendAll, extraSkip])
}

export { keysLabel }
