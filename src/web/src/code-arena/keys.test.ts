import { describe, expect, it } from 'vitest'
import { defaultKeybindings } from './default-keybindings'
import { buildRules, chordOf, evalWhen, keyFor, keysLabel, monacoKeybinding, parseKey, parseWhen, resolve, type Chord } from './keys'

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
