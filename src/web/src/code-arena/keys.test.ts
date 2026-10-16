import { describe, expect, it } from 'vitest'
import { defaultKeybindings } from './default-keybindings'
import { browserKeeps, buildRules, chordOf, evalWhen, keyFor, keysLabel, monacoDefaults, monacoKeybinding, parseKey, parseWhen, resolve, type Chord } from './keys'

const press = (key: string, code: string, mods: Partial<Record<'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey', boolean>> = {}) =>
  chordOf({ key, code, ctrlKey: false, shiftKey: false, altKey: false, metaKey: false, ...mods })!

describe('VS Code keys', () => {
  it('reads key strings, one or two chords, and the keys as the system shows them', () => {
    expect(parseKey('ctrl+shift+p')).toEqual([{ ctrl: true, shift: true, alt: false, meta: false, key: 'p' }])
    expect(parseKey('Ctrl+K Ctrl+S')!.map((c) => c.key)).toEqual(['k', 's'])
    expect(parseKey('cmd+alt+[KeyB]')).toEqual([{ ctrl: false, shift: false, alt: true, meta: true, key: 'b' }])
    expect(parseKey('ctrl+`')![0]!.key).toBe('`')
    expect(parseKey('shift+alt+f5')![0]!.key).toBe('f5')
    expect(parseKey('ctrl+nothing')).toBeNull()
    expect(parseKey('a b c')).toBeNull()
    expect(keysLabel(parseKey('ctrl+k ctrl+s')!, false)).toBe('Ctrl+K Ctrl+S')
    expect(keysLabel(parseKey('cmd+shift+p')!, true)).toBe('⇧⌘P')
    expect(keysLabel(parseKey('ctrl+alt+pageup')!, false)).toBe('Ctrl+Alt+PageUp')
  })

  it('reads a key press by the letter typed, and other keys by their place', () => {
    expect(press('p', 'KeyP', { ctrlKey: true })).toEqual({ ctrl: true, shift: false, alt: false, meta: false, key: 'p' })
    // A French keyboard's A is where the US Q is: the letter typed counts.
    expect(press('a', 'KeyQ', { ctrlKey: true }).key).toBe('a')
    expect(press('~', 'Backquote', { ctrlKey: true, shiftKey: true }).key).toBe('`')
    expect(press('F5', 'F5').key).toBe('f5')
    expect(press('ArrowLeft', 'ArrowLeft', { altKey: true }).key).toBe('left')
    expect(chordOf({ key: 'Control', code: 'ControlLeft', ctrlKey: true, shiftKey: false, altKey: false, metaKey: false })).toBeNull()
  })

  it('reads when clauses and decides them against the context', () => {
    const w = (text: string) => parseWhen(text).when!
    const ctx = { editorIsOpen: true, inputFocus: false, resourceLangId: 'python', resourceFilename: 'app.test.ts', count: 3, list: ['a'], item: 'a' }
    expect(evalWhen(w('editorIsOpen && !inputFocus'), ctx)).toBe(true)
    expect(evalWhen(w("resourceLangId == 'python' || false"), ctx)).toBe(true)
    expect(evalWhen(w('resourceLangId == typescript'), ctx)).toBe(false)
    expect(evalWhen(w('resourceLangId != typescript'), ctx)).toBe(true)
    expect(evalWhen(w('resourceFilename =~ /\\.test\\.ts$/'), ctx)).toBe(true)
    expect(evalWhen(w('count > 2 && count <= 3'), ctx)).toBe(true)
    expect(evalWhen(w('item in list'), ctx)).toBe(true)
    expect(evalWhen(w('item not in list'), ctx)).toBe(false)
    expect(evalWhen(w('!(editorIsOpen && inputFocus)'), ctx)).toBe(true)
    expect(evalWhen(w('missing'), ctx)).toBe(false)
    expect(parseWhen('editorIsOpen &&').error).toBeTruthy()
    expect(parseWhen('(a').error).toMatch(/\)/)
  })

  it('lets the last rule that matches win, takes defaults away with "-command", and waits for a chord\'s second key', () => {
    const user = [
      { key: 'ctrl+shift+e', command: 'workbench.action.togglePanel' },
      { key: 'ctrl+b', command: '-workbench.action.toggleSidebarVisibility' },
      { key: 'ctrl+k ctrl+b', command: 'workbench.action.toggleSidebarVisibility', when: 'editorIsOpen' },
      { key: 'nonsense+x', command: 'x' },
      { command: 'y' },
      { key: 'ctrl+y', command: 'not.a.command' },
      { key: 'ctrl+u', command: 'a', when: 'a ==' },
    ]
    const known = (id: string) => id !== 'not.a.command'
    const { rules, problems } = buildRules(defaultKeybindings, user, known)
    expect(problems.map((p) => [p.index, p.severity])).toEqual([
      [3, 'error'],
      [4, 'error'],
      [5, 'warning'],
      [6, 'error'],
    ])
    const at = (k: string) => parseKey(k)![0]!
    expect(resolve(rules, at('ctrl+shift+e'), {}, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.togglePanel' } })
    expect(resolve(rules, at('ctrl+b'), {}, null)).toEqual({ kind: 'none' })
    const first = resolve(rules, at('ctrl+k'), {}, null)
    expect(first.kind).toBe('chord')
    const pending = (first as { first: Chord }).first
    expect(resolve(rules, at('ctrl+s'), {}, pending)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.openGlobalKeybindings' } })
    // A chord whose when does not hold is no command.
    expect(resolve(rules, at('ctrl+b'), { editorIsOpen: false }, pending)).toEqual({ kind: 'none' })
    expect(resolve(rules, at('ctrl+b'), { editorIsOpen: true }, pending)).toMatchObject({ kind: 'command' })
    expect(keyFor(rules, 'workbench.action.toggleSidebarVisibility', false)).toBe('Ctrl+K Ctrl+B')
    // A default's first key is the one shown (VS Code's primary key), not its second.
    expect(keyFor(rules, 'workbench.action.showCommands', false)).toBe('Ctrl+Shift+P')
    expect(keyFor(rules, 'workbench.action.quickOpen', false)).toBe('Ctrl+P')
    // A rule a "when" leaves out is skipped for an earlier one.
    expect(resolve(rules, at('ctrl+w'), { editorIsOpen: false }, null)).toEqual({ kind: 'none' })
  })

  it('leaves out a rule of a command Code Arena does not have, so its key keeps the default', () => {
    const user = [
      { key: 'ctrl+`', command: 'workbench.action.terminal.focus' },
      { key: 'ctrl+p', command: 'cursorUp', when: 'editorTextFocus' },
    ]
    const known = (id: string) => id !== 'workbench.action.terminal.focus' && id !== 'cursorUp'
    const { rules, problems } = buildRules(defaultKeybindings, user, known)
    expect(problems).toEqual([{ index: 0, message: 'Code Arena has no command "workbench.action.terminal.focus": the rule does nothing here.', severity: 'warning' }])
    expect(rules.some((r) => r.command === 'workbench.action.terminal.focus')).toBe(false)
    expect(resolve(rules, parseKey('ctrl+`')![0]!, {}, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.terminal.toggleTerminal' } })
    // The editor's core commands (their ids have no dot) are not among its actions, but Monaco runs them: the rule stays.
    expect(rules.at(-1)).toMatchObject({ command: 'cursorUp', whenText: 'editorTextFocus', source: 'user' })
  })

  it('keeps the "-command" rules for Monaco, which take its own keys away, and runs none of them', () => {
    const user = [
      { key: 'ctrl+shift+k', command: '-editor.action.deleteLines' },
      { key: 'ctrl+d', command: '-editor.action.addSelectionToNextFindMatch', when: 'editorFocus' },
      { command: '-editor.action.copyLinesDownAction' },
      { key: 'ctrl+b', command: '-workbench.action.toggleSidebarVisibility' },
    ]
    const { rules, problems } = buildRules(defaultKeybindings, user, () => true)
    expect(problems).toEqual([])
    const KeyMod = { CtrlCmd: 2048, Shift: 1024, Alt: 512, WinCtrl: 256, chord: (a: number, b: number) => (a | (b << 16)) >>> 0 }
    const KeyCode = { KeyB: 32, KeyD: 34, KeyK: 41 }
    // As the editor gives them to Monaco's addKeybindingRules: a removal with no key takes all of its command's (0).
    const removals = rules.filter((r) => r.command.startsWith('-')).map((r) => ({ keybinding: monacoKeybinding(r.chords, { KeyMod, KeyCode }, false), command: r.command, when: r.whenText }))
    expect(removals).toEqual([
      { keybinding: 2048 | 1024 | 41, command: '-editor.action.deleteLines', when: undefined },
      { keybinding: 2048 | 34, command: '-editor.action.addSelectionToNextFindMatch', when: 'editorFocus' },
      { keybinding: 0, command: '-editor.action.copyLinesDownAction', when: undefined },
      { keybinding: 2048 | 32, command: '-workbench.action.toggleSidebarVisibility', when: undefined },
    ])
    const at = (k: string) => parseKey(k)![0]!
    expect(resolve(rules, at('ctrl+shift+k'), {}, null)).toEqual({ kind: 'none' })
    expect(resolve(rules, at('ctrl+b'), {}, null)).toEqual({ kind: 'none' })
    expect(keyFor(rules, 'workbench.action.toggleSidebarVisibility', false)).toBeUndefined()
  })

  it("takes a default away with VS Code's own when for it, which reads context keys Code Arena does not have", () => {
    const user = [
      { key: 'ctrl+shift+b', command: '-workbench.action.tasks.build', when: 'taskCommandsRegistered' },
      { key: 'f5', command: '-workbench.action.debug.start', when: "debuggersAvailable && debugState == 'inactive'" },
      // A when of Code Arena's own context keys is taken as written: this one is no default's, and takes nothing away.
      { key: 'ctrl+b', command: '-workbench.action.toggleSidebarVisibility', when: 'terminalFocus' },
    ]
    const { rules } = buildRules(defaultKeybindings, user)
    const at = (k: string) => parseKey(k)![0]!
    expect(resolve(rules, at('ctrl+shift+b'), {}, null)).toEqual({ kind: 'none' })
    expect(resolve(rules, at('f5'), { inDebugMode: false, debugState: 'inactive' }, null)).toEqual({ kind: 'none' })
    expect(resolve(rules, at('f5'), { inDebugMode: true, debugState: 'stopped' }, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.debug.continue' } })
    expect(resolve(rules, at('ctrl+b'), { terminalFocus: true }, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.toggleSidebarVisibility' } })
  })

  it("lists Monaco's own keys among the defaults, which keybindings.json takes away and the workbench's win over", () => {
    // As Monaco's keybinding service has them: its defaults in its resolver.
    const kb = (key: string, command: string, when?: string) => ({ command, when: when ? { serialize: () => when } : undefined, resolvedKeybinding: { getUserSettingsLabel: () => key } })
    const ed = { _standaloneKeybindingService: { _getResolver: () => ({ _defaultKeybindings: [kb('ctrl+d', 'editor.action.addSelectionToNextFindMatch', 'editorFocus'), kb('f1', 'editor.action.quickCommand'), kb('ctrl+k ctrl+c', 'editor.action.addCommentLine', 'editorTextFocus && !editorReadonly'), kb('ctrl+shift+p', 'editor.action.x', 'false')] }) } }
    const editor = monacoDefaults(ed)
    // F1 is the workbench's palette's (Monaco's own is taken away), and a key that never applies is none.
    expect(editor).toEqual([
      { key: 'ctrl+d', command: 'editor.action.addSelectionToNextFindMatch', when: 'editorFocus' },
      { key: 'ctrl+k ctrl+c', command: 'editor.action.addCommentLine', when: 'editorTextFocus && !editorReadonly' },
    ])
    expect(monacoDefaults({})).toEqual([])
    const shown = buildRules(defaultKeybindings, [], undefined, [], editor).rules
    expect(shown.filter((r) => r.source === 'editor').map((r) => r.command)).toEqual(['editor.action.addSelectionToNextFindMatch', 'editor.action.addCommentLine'])
    expect(keyFor(shown, 'editor.action.addSelectionToNextFindMatch', false)).toBe('Ctrl+D')
    expect(keyFor(shown, 'editor.action.addCommentLine', false)).toBe('Ctrl+K Ctrl+C')
    // A rule of Monaco's and one of the workbench's for the same key: the workbench's wins (the last that matches).
    const both = buildRules([{ key: 'ctrl+d', command: 'workbench.action.x' }], [], undefined, [], editor).rules
    expect(resolve(both, parseKey('ctrl+d')![0]!, { editorFocus: true }, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.x' } })
    // "-command" takes Monaco's key away from the lists, and is kept for Monaco.
    const { rules } = buildRules(defaultKeybindings, [{ key: 'ctrl+d', command: '-editor.action.addSelectionToNextFindMatch' }], undefined, [], editor)
    expect(rules.some((r) => r.command === 'editor.action.addSelectionToNextFindMatch')).toBe(false)
    expect(rules.at(-1)).toMatchObject({ command: '-editor.action.addSelectionToNextFindMatch', key: 'ctrl+d' })
    expect(keyFor(rules, 'editor.action.addSelectionToNextFindMatch', false)).toBeUndefined()
  })

  it("has quick diff's keys in the keymap, so keybindings.json changes and takes them away", () => {
    const { rules } = buildRules(defaultKeybindings, [])
    const at = (k: string) => parseKey(k)![0]!
    const inEditor = { editorTextFocus: true }
    expect(resolve(rules, at('alt+f3'), inEditor, null)).toMatchObject({ kind: 'command', rule: { command: 'editor.action.dirtydiff.next' } })
    expect(resolve(rules, at('shift+alt+f3'), inEditor, null)).toMatchObject({ kind: 'command', rule: { command: 'editor.action.dirtydiff.previous' } })
    // Alt+F5 is a compare's next change in the compare, quick diff's in the code editor.
    expect(resolve(rules, at('alt+f5'), inEditor, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.editor.nextChange' } })
    expect(resolve(rules, at('shift+alt+f5'), inEditor, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.editor.previousChange' } })
    expect(resolve(rules, at('alt+f5'), { editorTextFocus: true, textCompareEditorVisible: true }, null)).toMatchObject({ kind: 'command', rule: { command: 'workbench.action.compareEditor.nextChange' } })
    const removed = buildRules(defaultKeybindings, [{ key: 'alt+f3', command: '-editor.action.dirtydiff.next' }]).rules
    expect(resolve(removed, at('alt+f3'), inEditor, null)).toEqual({ kind: 'none' })
  })

  it('knows the keys the browser keeps in a tab, as a chord\'s second key too, and shows another key of the command in menus', () => {
    expect(browserKeeps(parseKey('ctrl+w')!, false)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+k ctrl+w')!, false)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+k ctrl+t')!, false)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+k ctrl+n')!, false)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+f4')!, false)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+shift+t')!, false)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+alt+w')!, false)).toBe(false)
    expect(browserKeeps(parseKey('ctrl+k ctrl+alt+w')!, false)).toBe(false)
    // On macOS, ⌘ is the browser's: Ctrl+W reaches the page.
    expect(browserKeeps(parseKey('cmd+k cmd+w')!, true)).toBe(true)
    expect(browserKeeps(parseKey('ctrl+w')!, true)).toBe(false)
    const { rules } = buildRules(defaultKeybindings, [])
    expect(keyFor(rules, 'workbench.action.closeActiveEditor', false)).toBe('Ctrl+Alt+W')
    expect(keyFor(rules, 'workbench.action.closeAllEditors', false)).toBe('Ctrl+K Ctrl+Alt+W')
    expect(keyFor(rules, 'workbench.action.selectTheme', false)).toBe('Ctrl+K Ctrl+Alt+T')
    expect(keyFor(rules, 'git.unstageSelectedRanges', false)).toBe('Ctrl+K Ctrl+Alt+N')
    expect(keyFor(rules, 'workbench.action.reopenClosedEditor', false)).toBe('Ctrl+Shift+Alt+T')
    // Every command whose default the browser keeps has one it does not.
    const commands = new Set(rules.map((r) => r.command))
    for (const c of commands) {
      const keys = rules.filter((r) => r.command === c)
      if (keys.some((r) => browserKeeps(r.chords, false))) expect(keys.some((r) => !browserKeeps(r.chords, false)), c).toBe(true)
    }
    // Ctrl+N has no command: VS Code's new untitled file is none of Code Arena's.
    expect(rules.some((r) => r.command === 'workbench.action.files.newUntitledFile')).toBe(false)
  })

  it('gives Monaco the same keys, ⌘ as CtrlCmd on macOS', () => {
    const KeyMod = { CtrlCmd: 2048, Shift: 1024, Alt: 512, WinCtrl: 256, chord: (a: number, b: number) => (a | (b << 16)) >>> 0 }
    const KeyCode = { KeyK: 41, KeyS: 49, KeyP: 46, Backquote: 92, F1: 59 }
    expect(monacoKeybinding(parseKey('ctrl+shift+p')!, { KeyMod, KeyCode }, false)).toBe(2048 | 1024 | 46)
    expect(monacoKeybinding(parseKey('cmd+shift+p')!, { KeyMod, KeyCode }, true)).toBe(2048 | 1024 | 46)
    expect(monacoKeybinding(parseKey('ctrl+`')!, { KeyMod, KeyCode }, true)).toBe(256 | 92)
    expect(monacoKeybinding(parseKey('ctrl+k ctrl+s')!, { KeyMod, KeyCode }, false)).toBe(KeyMod.chord(2048 | 41, 2048 | 49))
    expect(monacoKeybinding(parseKey('ctrl+numpad_add')!, { KeyMod, KeyCode }, false)).toBeNull()
  })

  it('has no default key twice for different commands in the same context', () => {
    const { rules } = buildRules(defaultKeybindings, [])
    const seen = new Map<string, string>()
    for (const r of rules) {
      const k = `${keysLabel(r.chords, false)}|${r.whenText ?? ''}`
      if (seen.has(k)) expect(seen.get(k)).toBe(r.command)
      seen.set(k, r.command)
    }
    // Every default reads.
    expect(rules).toHaveLength(defaultKeybindings.length)
  })
})
