// Keyboard shortcuts as VS Code has them: its key strings ("ctrl+k ctrl+s", "cmd+shift+p"), its when clauses, its
// keybindings.json rules (the last that matches wins; "-command" takes a default away), chords of two keys. The page's
// own resolver reads them outside the code editor; inside it, Monaco's resolver gets the same rules (monacoKeybinding).

/** One key press: its modifiers, and the key by VS Code's name for it ("a", "1", "f5", "`", "pagedown", "enter"). */
export interface Chord {
  ctrl: boolean
  shift: boolean
  alt: boolean
  meta: boolean
  key: string
}

/** A rule of a keymap, read: one or two chords, the command (and its argument), when it applies, and whose rule it is. */
export interface Rule {
  chords: Chord[]
  command: string
  args?: unknown
  when?: When
  whenText?: string
  source: 'default' | 'user' | 'extension'
  /** The key as written, for the Keyboard Shortcuts editor. */
  key: string
}

/** A rule as keybindings.json and the defaults write it. */
export interface RuleJson {
  key: string
  command: string
  when?: string
  args?: unknown
  /** The macOS key, when it is not the key with Ctrl as ⌘ (the defaults only). */
  mac?: string
  /** The Linux key, when it differs (the defaults only). */
  linux?: string
  /** The Windows key, when it differs (the defaults only). */
  win?: string
}

/** macOS (and iPadOS): ⌘ is the editor's modifier there. */
export const isMac = () => typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)

const named: Record<string, string> = {
  esc: 'escape', return: 'enter', del: 'delete', ins: 'insert', pgup: 'pageup', pgdn: 'pagedown', arrowup: 'up', arrowdown: 'down', arrowleft: 'left', arrowright: 'right',
  oem_3: '`', oem_minus: '-', oem_plus: '=', oem_4: '[', oem_6: ']', oem_5: '\\', oem_1: ';', oem_7: "'", oem_comma: ',', oem_period: '.', oem_2: '/',
}

const keyNames = new Set([
  ...'abcdefghijklmnopqrstuvwxyz0123456789'.split(''),
  '`', '-', '=', '[', ']', '\\', ';', "'", ',', '.', '/',
  'left', 'up', 'right', 'down', 'pageup', 'pagedown', 'home', 'end', 'insert', 'delete', 'backspace', 'tab', 'enter', 'escape', 'space', 'pausebreak', 'capslock', 'contextmenu',
  ...Array.from({ length: 19 }, (_, i) => `f${i + 1}`),
  ...Array.from({ length: 10 }, (_, i) => `numpad${i}`),
  'numpad_multiply', 'numpad_add', 'numpad_separator', 'numpad_subtract', 'numpad_decimal', 'numpad_divide',
])

/** VS Code's key string, read: one or two chords; null when a part is not a key it knows. */
export function parseKey(text: string): Chord[] | null {
  const parts = text.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (parts.length === 0 || parts.length > 2) return null
  const chords: Chord[] = []
  for (const part of parts) {
    const c: Chord = { ctrl: false, shift: false, alt: false, meta: false, key: '' }
    // "ctrl+=" and "ctrl++": the key may itself be "+" … not in VS Code's names, but "=" is.
    const bits = part.split('+')
    if (part.endsWith('++')) bits.splice(-2, 2, '+')
    for (let k = 0; k < bits.length; k++) {
      const bit = bits[k]!
      if (k < bits.length - 1) {
        if (bit === 'ctrl' || bit === 'control') c.ctrl = true
        else if (bit === 'shift') c.shift = true
        else if (bit === 'alt' || bit === 'option') c.alt = true
        else if (bit === 'cmd' || bit === 'meta' || bit === 'win' || bit === 'super') c.meta = true
        else return null
      } else {
        // "[KeyA]": a key by where it is, as VS Code also writes them.
        const code = /^\[(.+)\]$/.exec(bit)
        const key = code ? keyOfCode(code[1]!, '') : (named[bit] ?? bit)
        if (!key || !keyNames.has(key)) return null
        c.key = key
      }
    }
    chords.push(c)
  }
  return chords
}

const codes: Record<string, string> = {
  backquote: '`', minus: '-', equal: '=', bracketleft: '[', bracketright: ']', backslash: '\\', semicolon: ';', quote: "'", comma: ',', period: '.', slash: '/',
  arrowleft: 'left', arrowup: 'up', arrowright: 'right', arrowdown: 'down', pageup: 'pageup', pagedown: 'pagedown', home: 'home', end: 'end', insert: 'insert', delete: 'delete',
  backspace: 'backspace', tab: 'tab', enter: 'enter', numpadenter: 'enter', escape: 'escape', space: 'space', pause: 'pausebreak', capslock: 'capslock', contextmenu: 'contextmenu',
  numpadmultiply: 'numpad_multiply', numpadadd: 'numpad_add', numpadcomma: 'numpad_separator', numpadsubtract: 'numpad_subtract', numpaddecimal: 'numpad_decimal', numpaddivide: 'numpad_divide',
}

/** A key by its place on the keyboard (KeyboardEvent.code), else by what it types (key). */
function keyOfCode(code: string, key: string): string | null {
  const c = code.toLowerCase()
  if (/^key[a-z]$/.test(c)) return c.slice(3)
  if (/^digit\d$/.test(c)) return c.slice(5)
  if (/^numpad\d$/.test(c)) return c
  if (/^f\d{1,2}$/.test(c)) return c
  if (c in codes) return codes[c]!
  const k = key.toLowerCase()
  if (k === ' ') return 'space'
  if (/^f\d{1,2}$/.test(k)) return k
  if (k.length === 1 && keyNames.has(k)) return k
  const byName = codes[k] ?? named[k]
  return byName && keyNames.has(byName) ? byName : null
}

/** The chord a key press is; null for a modifier alone. Letters are the layout's (as VS Code dispatches them), other keys by their place. */
export function chordOf(e: Pick<KeyboardEvent, 'key' | 'code' | 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey'>): Chord | null {
  if (['Control', 'Shift', 'Alt', 'Meta', 'OS', 'AltGraph'].includes(e.key)) return null
  const letter = e.key.length === 1 && /[a-z]/i.test(e.key) && !e.altKey ? e.key.toLowerCase() : null
  const key = letter ?? keyOfCode(e.code ?? '', e.key)
  if (!key) return null
  return { ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey, meta: e.metaKey, key }
}

export const sameChord = (a: Chord, b: Chord) => a.key === b.key && a.ctrl === b.ctrl && a.shift === b.shift && a.alt === b.alt && a.meta === b.meta

const display: Record<string, string> = {
  left: '←', up: '↑', right: '→', down: '↓', pageup: 'PageUp', pagedown: 'PageDown', escape: 'Escape', enter: 'Enter', backspace: 'Backspace', delete: 'Delete', insert: 'Insert',
  home: 'Home', end: 'End', tab: 'Tab', space: 'Space', capslock: 'CapsLock', pausebreak: 'PauseBreak', contextmenu: 'ContextMenu',
}

const keyLabel = (key: string) => display[key] ?? (/^f\d+$/.test(key) ? key.toUpperCase() : /^numpad/.test(key) ? `Numpad${key.slice(6).replace('_', ' ')}` : key.toUpperCase())

/** A chord as the system shows it: "Ctrl+Shift+P" or "⇧⌘P". */
export function chordLabel(c: Chord, mac = isMac()): string {
  if (mac) return `${c.ctrl ? '⌃' : ''}${c.alt ? '⌥' : ''}${c.shift ? '⇧' : ''}${c.meta ? '⌘' : ''}${keyLabel(c.key)}`
  return [c.ctrl && 'Ctrl', c.shift && 'Shift', c.alt && 'Alt', c.meta && (navigator.platform.startsWith('Win') ? 'Win' : 'Meta'), keyLabel(c.key)].filter(Boolean).join('+')
}

/** Chords as the system shows them, space apart: "Ctrl+K Ctrl+S", "⌘K ⌘S". */
export const keysLabel = (chords: Chord[], mac = isMac()) => chords.map((c) => chordLabel(c, mac)).join(' ')

/** A chord as VS Code writes it in keybindings.json: "ctrl+shift+p". */
export const chordText = (c: Chord) => [c.ctrl && 'ctrl', c.shift && 'shift', c.alt && 'alt', c.meta && (isMac() ? 'cmd' : 'meta'), c.key].filter(Boolean).join('+')

// ----- when clauses -----

export type When =
  | { op: 'true' }
  | { op: 'false' }
  | { op: 'has'; key: string }
  | { op: 'not'; expr: When }
  | { op: 'and' | 'or'; exprs: When[] }
  | { op: 'eq' | 'ne'; key: string; value: string | boolean | number }
  | { op: 'lt' | 'le' | 'gt' | 'ge'; key: string; value: number }
  | { op: 'regex'; key: string; regex: RegExp }
  | { op: 'in' | 'notIn'; key: string; list: string }

/** A when clause read, or why not. */
export function parseWhen(text: string): { when?: When; error?: string } {
  const tokens: string[] = []
  const re = /\s*(&&|\|\||===|!==|==|!=|=~|<=|>=|<|>|!|\(|\)|'(?:[^'\\]|\\.)*'|\/(?:[^/\\]|\\.)+\/[gimsuy]*|[^\s()!&|=<>~']+)/y
  let pos = 0
  const src = text.trim()
  while (pos < src.length) {
    re.lastIndex = pos
    const m = re.exec(src)
    if (!m) return { error: `Not a when clause at "${src.slice(pos, pos + 12)}".` }
    tokens.push(m[1]!)
    pos = re.lastIndex
    while (pos < src.length && /\s/.test(src[pos]!)) pos++
  }
  let t = 0
  const peek = () => tokens[t]
  const take = () => tokens[t++]
  const literal = (tok: string | undefined): string | boolean | number => {
    if (tok === undefined) throw new Error('A value was expected.')
    if (tok === 'true') return true
    if (tok === 'false') return false
    if (/^'.*'$/.test(tok)) return tok.slice(1, -1).replace(/\\(.)/g, '$1')
    if (/^-?\d+(\.\d+)?$/.test(tok)) return Number(tok)
    return tok
  }
  const primary = (): When => {
    const tok = take()
    if (tok === undefined) throw new Error('The clause ends too soon.')
    if (tok === '!') return { op: 'not', expr: primary() }
    if (tok === '(') {
      const e = or()
      if (take() !== ')') throw new Error('A ")" is missing.')
      return e
    }
    if (tok === 'true') return { op: 'true' }
    if (tok === 'false') return { op: 'false' }
    if (!/^[A-Za-z_$][\w.:$-]*$/.test(tok)) throw new Error(`"${tok}" is not a context key.`)
    const op = peek()
    if (op === '==' || op === '===' || op === '!=' || op === '!==') {
      take()
      const value = literal(take())
      return { op: op.startsWith('!') ? 'ne' : 'eq', key: tok, value }
    }
    if (op === '<' || op === '<=' || op === '>' || op === '>=') {
      take()
      const value = literal(take())
      if (typeof value !== 'number') throw new Error(`"${tok} ${op}" compares with a number.`)
      return { op: op === '<' ? 'lt' : op === '<=' ? 'le' : op === '>' ? 'gt' : 'ge', key: tok, value }
    }
    if (op === '=~') {
      take()
      const lit = take()
      const m = lit && /^\/(.*)\/([gimsuy]*)$/.exec(lit)
      if (!m) throw new Error('"=~" takes a /regular expression/.')
      return { op: 'regex', key: tok, regex: new RegExp(m[1]!, m[2]!.replace('g', '')) }
    }
    if (op === 'in') {
      take()
      return { op: 'in', key: tok, list: String(literal(take())) }
    }
    if (op === 'not' && tokens[t + 1] === 'in') {
      t += 2
      return { op: 'notIn', key: tok, list: String(literal(take())) }
    }
    return { op: 'has', key: tok }
  }
  const and = (): When => {
    const exprs = [primary()]
    while (peek() === '&&') {
      take()
      exprs.push(primary())
    }
    return exprs.length === 1 ? exprs[0]! : { op: 'and', exprs }
  }
  const or = (): When => {
    const exprs = [and()]
    while (peek() === '||') {
      take()
      exprs.push(and())
    }
    return exprs.length === 1 ? exprs[0]! : { op: 'or', exprs }
  }
  try {
    if (tokens.length === 0) return { when: { op: 'true' } }
    const when = or()
    if (t < tokens.length) return { error: `Unexpected "${tokens[t]}".` }
    return { when }
  } catch (e) {
    return { error: (e as Error).message }
  }
}

/** The context keys a when clause reads: names to values. */
export type Context = Record<string, unknown>

export function evalWhen(w: When | undefined, ctx: Context): boolean {
  if (!w) return true
  switch (w.op) {
    case 'true':
      return true
    case 'false':
      return false
    case 'has':
      return !!ctx[w.key]
    case 'not':
      return !evalWhen(w.expr, ctx)
    case 'and':
      return w.exprs.every((e) => evalWhen(e, ctx))
    case 'or':
      return w.exprs.some((e) => evalWhen(e, ctx))
    case 'eq':
    case 'ne': {
      const v = ctx[w.key]
      const equal = typeof w.value === 'boolean' ? !!v === w.value : String(v ?? '') === String(w.value)
      return w.op === 'eq' ? equal : !equal
    }
    case 'lt':
    case 'le':
    case 'gt':
    case 'ge': {
      const v = Number(ctx[w.key])
      if (Number.isNaN(v)) return false
      return w.op === 'lt' ? v < w.value : w.op === 'le' ? v <= w.value : w.op === 'gt' ? v > w.value : v >= w.value
    }
    case 'regex':
      return w.regex.test(String(ctx[w.key] ?? ''))
    case 'in':
    case 'notIn': {
      const list = ctx[w.list]
      const v = ctx[w.key]
      const has = Array.isArray(list) ? list.includes(v) : list && typeof list === 'object' ? Object.hasOwn(list, String(v)) : false
      return w.op === 'in' ? has : !has
    }
  }
}

// ----- the keymap -----

/** A rule of keybindings.json that could not be read: its index, and why. */
export interface RuleProblem {
  index: number
  message: string
  severity: 'error' | 'warning'
}

/** A default's key on this system. */
export const keyOnThisSystem = (r: RuleJson, mac = isMac()) =>
  mac ? (r.mac ?? r.key.replace(/\bctrl\+/g, 'cmd+')) : typeof navigator !== 'undefined' && navigator.platform.startsWith('Win') ? (r.win ?? r.key) : (r.linux ?? r.key)

/**
 * The rules that apply: the defaults, then the extensions', then the person's, each "-command" rule taking away the
 * earlier rules of that command (only those of its key and when, when it says them).
 */
export function buildRules(defaults: RuleJson[], user: unknown[], known?: (id: string) => boolean, extension: RuleJson[] = []): { rules: Rule[]; problems: RuleProblem[] } {
  const rules: Rule[] = []
  const problems: RuleProblem[] = []
  const read = (r: RuleJson, source: Rule['source'], key: string) => {
    const chords = parseKey(key)
    const parsed = r.when ? parseWhen(r.when) : {}
    if (!chords || parsed.error) return null
    return { chords, command: r.command, args: r.args, when: parsed.when, whenText: r.when, source, key } satisfies Rule
  }
  for (const r of defaults) {
    const rule = read(r, 'default', keyOnThisSystem(r))
    if (rule) rules.push(rule)
  }
  for (const r of extension) {
    const rule = read(r, 'extension', r.key)
    if (rule) rules.push(rule)
  }
  user.forEach((raw, index) => {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return problems.push({ index, message: 'Each rule is an object: {"key", "command", "when"}.', severity: 'error' })
    const r = raw as Partial<RuleJson>
    if (typeof r.command !== 'string') return problems.push({ index, message: 'The rule has no "command".', severity: 'error' })
    if (r.when !== undefined && typeof r.when !== 'string') return problems.push({ index, message: '"when" is a text.', severity: 'error' })
    const parsed = r.when ? parseWhen(r.when) : {}
    if (parsed.error) return problems.push({ index, message: `"when": ${parsed.error}`, severity: 'error' })
    if (r.command.startsWith('-')) {
      const command = r.command.slice(1)
      const chords = r.key ? parseKey(r.key) : null
      if (r.key && !chords) return problems.push({ index, message: `"${r.key}" is not a key.`, severity: 'error' })
      for (let k = rules.length - 1; k >= 0; k--) {
        const x = rules[k]!
        if (x.command !== command) continue
        if (chords && (x.chords.length !== chords.length || !x.chords.every((c, i) => sameChord(c, chords[i]!)))) continue
        if (r.when && (x.whenText ?? '') !== r.when) continue
        rules.splice(k, 1)
      }
      return
    }
    if (typeof r.key !== 'string') return problems.push({ index, message: 'The rule has no "key".', severity: 'error' })
    const chords = parseKey(r.key)
    if (!chords) return problems.push({ index, message: `"${r.key}" is not a key.`, severity: 'error' })
    if (known && r.command && !known(r.command)) problems.push({ index, message: `Code Arena has no command "${r.command}": the rule does nothing here.`, severity: 'warning' })
    if (!r.command) return
    rules.push({ chords, command: r.command, args: r.args, when: parsed.when, whenText: r.when, source: 'user', key: r.key })
  })
  return { rules, problems }
}

/** What a key press is, with these rules: a command to run, the first key of a chord, or nothing of theirs. */
export type Resolution = { kind: 'command'; rule: Rule } | { kind: 'chord'; first: Chord } | { kind: 'none' }

/** The last rule that matches wins, as in VS Code; a rule of two chords whose first is pressed waits for the second. */
export function resolve(rules: Rule[], pressed: Chord, ctx: Context, pending: Chord | null, accept: (r: Rule) => boolean = () => true): Resolution {
  for (let k = rules.length - 1; k >= 0; k--) {
    const r = rules[k]!
    if (!accept(r)) continue
    if (pending) {
      if (r.chords.length === 2 && sameChord(r.chords[0]!, pending) && sameChord(r.chords[1]!, pressed) && evalWhen(r.when, ctx)) return { kind: 'command', rule: r }
    } else if (sameChord(r.chords[0]!, pressed) && evalWhen(r.when, ctx)) {
      return r.chords.length === 2 ? { kind: 'chord', first: pressed } : { kind: 'command', rule: r }
    }
  }
  return { kind: 'none' }
}

/** The key of a command (its last rule's, as VS Code shows it), for menus and tooltips. */
export function keyFor(rules: Rule[], command: string, mac = isMac()): string | undefined {
  const r = rules.findLast((x) => x.command === command && !x.args)
  return r ? keysLabel(r.chords, mac) : undefined
}

// ----- Monaco -----

const monacoCodes: Record<string, string> = {
  '`': 'Backquote', '-': 'Minus', '=': 'Equal', '[': 'BracketLeft', ']': 'BracketRight', '\\': 'Backslash', ';': 'Semicolon', "'": 'Quote', ',': 'Comma', '.': 'Period', '/': 'Slash',
  left: 'LeftArrow', up: 'UpArrow', right: 'RightArrow', down: 'DownArrow', pageup: 'PageUp', pagedown: 'PageDown', home: 'Home', end: 'End', insert: 'Insert', delete: 'Delete',
  backspace: 'Backspace', tab: 'Tab', enter: 'Enter', escape: 'Escape', space: 'Space', pausebreak: 'PauseBreak', capslock: 'CapsLock', contextmenu: 'ContextMenu',
  numpad_multiply: 'NumpadMultiply', numpad_add: 'NumpadAdd', numpad_separator: 'NUMPAD_SEPARATOR', numpad_subtract: 'NumpadSubtract', numpad_decimal: 'NumpadDecimal', numpad_divide: 'NumpadDivide',
}

interface MonacoKeys {
  KeyMod: { CtrlCmd: number; Shift: number; Alt: number; WinCtrl: number; chord: (a: number, b: number) => number }
  KeyCode: Record<string, number | string>
}

/** Chords as Monaco's keybinding number; null for a key Monaco has no code for. */
export function monacoKeybinding(chords: Chord[], m: MonacoKeys, mac = isMac()): number | null {
  const one = (c: Chord) => {
    const name = /^[a-z]$/.test(c.key) ? `Key${c.key.toUpperCase()}` : /^\d$/.test(c.key) ? `Digit${c.key}` : /^f\d+$/.test(c.key) ? c.key.toUpperCase() : /^numpad\d$/.test(c.key) ? `Numpad${c.key.slice(6)}` : monacoCodes[c.key]
    const code = name ? m.KeyCode[name] : undefined
    if (typeof code !== 'number') return null
    let n = code
    // CtrlCmd is ⌘ on macOS and Ctrl elsewhere; WinCtrl is Ctrl on macOS and the Windows key elsewhere.
    if (mac ? c.meta : c.ctrl) n |= m.KeyMod.CtrlCmd
    if (mac ? c.ctrl : c.meta) n |= m.KeyMod.WinCtrl
    if (c.shift) n |= m.KeyMod.Shift
    if (c.alt) n |= m.KeyMod.Alt
    return n
  }
  const first = one(chords[0]!)
  if (first === null) return null
  if (chords.length === 1) return first
  const second = one(chords[1]!)
  return second === null ? null : m.KeyMod.chord(first, second)
}
