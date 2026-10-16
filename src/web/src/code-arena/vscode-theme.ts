import type * as Monaco from 'monaco-editor'
import { parse } from './jsonc'

// VS Code's colour themes (an extension's themes/*.json) as Monaco's: "colors" are the workbench's and the editor's
// colours by VS Code's ids (Monaco's are the same ids), "tokenColors" TextMate rules by scope. The editor's highlighting
// is Monaco's own (Monarch), whose tokens are named like TextMate's broadest scopes (comment, string, keyword…): a rule
// for "comment" colours them, and the common narrower scopes (constant.numeric, entity.name.type…) are given Monarch's
// names too.

/** A TextMate rule of a theme: the scopes it colours, and how. */
export interface TokenColor {
  name?: string
  scope?: string | string[]
  settings?: { foreground?: string; background?: string; fontStyle?: string }
}

/** A theme read whole: its own colours and rules over those of the theme it includes. */
export interface ColorThemeData {
  colors: Record<string, string>
  tokenColors: TokenColor[]
}

/** "#rgb", "#rgba", "#rrggbb" or "#rrggbbaa" as "#rrggbb" or "#rrggbbaa"; null for anything else. */
export function normalizeColor(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const v = value.trim().toLowerCase()
  if (/^#[0-9a-f]{6}([0-9a-f]{2})?$/.test(v)) return v
  if (/^#[0-9a-f]{3,4}$/.test(v)) return `#${[...v.slice(1)].map((c) => c + c).join('')}`
  return null
}

/** A path a theme gives ("./base.json"), from the folder of the file that gives it, as a path in the extension; null when it leads out. */
export function resolvePath(from: string, path: string): string | null {
  if (!path || path.includes(':') || path.startsWith('/')) return null
  const parts = from.split('/').filter(Boolean)
  for (const part of path.replace(/\\/g, '/').split('/')) {
    if (part === '' || part === '.') continue
    if (part === '..') {
      if (parts.length === 0) return null
      parts.pop()
    } else parts.push(part)
  }
  return parts.length ? parts.join('/') : null
}

const dirOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')

function readJson(text: string): unknown {
  const { value, errors } = parse(text)
  if (errors.length > 0 && value === undefined) throw new Error(`not JSON: ${errors[0]!.message}`)
  return value
}

/**
 * A theme's file and what it includes ("include", and "tokenColors" given as another file), read with `read` (a path in
 * the extension to its text). Ten includes deep at most.
 */
export async function loadColorTheme(path: string, read: (path: string) => Promise<string>, depth = 0): Promise<ColorThemeData> {
  const json = readJson(await read(path))
  if (!json || typeof json !== 'object' || Array.isArray(json)) throw new Error(`${path} holds no theme.`)
  const theme = json as { include?: unknown; colors?: unknown; tokenColors?: unknown }
  const dir = dirOf(path)
  let base: ColorThemeData = { colors: {}, tokenColors: [] }
  if (typeof theme.include === 'string' && depth < 10) {
    const included = resolvePath(dir, theme.include)
    if (included) base = await loadColorTheme(included, read, depth + 1)
  }
  const colors = { ...base.colors }
  if (theme.colors && typeof theme.colors === 'object' && !Array.isArray(theme.colors)) {
    for (const [id, value] of Object.entries(theme.colors as Record<string, unknown>)) {
      if (typeof value === 'string') colors[id] = value
      else if (value === null) delete colors[id]
    }
  }
  let own: TokenColor[] = []
  if (Array.isArray(theme.tokenColors)) own = theme.tokenColors as TokenColor[]
  else if (typeof theme.tokenColors === 'string' && depth < 10) {
    // Rules in a file of their own: an array, or an object holding "tokenColors" (or a .tmTheme's "settings").
    const file = resolvePath(dir, theme.tokenColors)
    if (file) {
      const rules = readJson(await read(file)) as { tokenColors?: unknown; settings?: unknown } | unknown[]
      own = (Array.isArray(rules) ? rules : Array.isArray(rules?.tokenColors) ? rules.tokenColors : Array.isArray(rules?.settings) ? rules.settings : []) as TokenColor[]
    }
  }
  return { colors, tokenColors: [...base.tokenColors, ...own.filter((r) => r && typeof r === 'object')] }
}

/** The narrower TextMate scopes Monaco's own highlighting names otherwise: their colour goes to Monarch's token too. */
const monarchNames: [scope: string, token: string][] = [
  ['constant.numeric', 'number'],
  ['string.regexp', 'regexp'],
  ['keyword.operator', 'operator'],
  ['keyword.control', 'keyword'],
  ['storage.type', 'keyword'],
  ['storage', 'keyword'],
  ['entity.name.type', 'type'],
  ['entity.name.class', 'type'],
  ['support.type', 'type'],
  ['support.class', 'type'],
  ['entity.name.type', 'type.identifier'],
  ['entity.name.tag', 'tag'],
  ['entity.other.attribute-name', 'attribute.name'],
  ['string', 'attribute.value'],
  ['punctuation', 'delimiter'],
  ['meta.brace', 'delimiter.bracket'],
  ['variable', 'variable'],
  ['support.function', 'predefined'],
  ['comment.block.documentation', 'comment.doc'],
  ['markup.heading', 'keyword.md'],
  ['markup.bold', 'strong'],
  ['markup.italic', 'emphasis'],
]

const styles = new Set(['italic', 'bold', 'underline', 'strikethrough'])

type Rule = { foreground?: string; background?: string; fontStyle?: string }

/** A rule's settings as Monaco's: colours of six hex digits (Monaco refuses any other), the font styles it knows. */
function ruleOf(s: TokenColor['settings']): Rule | null {
  if (!s || typeof s !== 'object') return null
  const rule: Rule = {}
  const fg = normalizeColor(s.foreground)
  const bg = normalizeColor(s.background)
  if (fg) rule.foreground = fg.slice(1, 7)
  if (bg) rule.background = bg.slice(1, 7)
  if (typeof s.fontStyle === 'string') rule.fontStyle = s.fontStyle.split(/\s+/).filter((w) => styles.has(w)).join(' ')
  return Object.keys(rule).length ? rule : null
}

/** The scopes a rule names: "a, b" or ["a", "b"]; a selector of several scopes ("source.js string") or an exclusion is left out. */
function scopesOf(scope: TokenColor['scope']): string[] {
  const list = Array.isArray(scope) ? scope : typeof scope === 'string' ? scope.split(',') : []
  return list.map((s) => (typeof s === 'string' ? s.trim() : '')).filter((s) => /^[\w][\w.-]*$/.test(s))
}

/** VS Code's uiTheme as Monaco's base theme. */
export function baseOf(uiTheme: string | undefined): Monaco.editor.BuiltinTheme {
  return uiTheme === 'vs' || uiTheme === 'hc-black' || uiTheme === 'hc-light' ? uiTheme : 'vs-dark'
}

/** A VS Code theme as Monaco's: its colours (valid ones), and its rules by scope (with Monarch's names for the common ones). */
export function toMonacoTheme(theme: ColorThemeData, uiTheme: string | undefined): Monaco.editor.IStandaloneThemeData {
  const colors: Record<string, string> = {}
  for (const [id, value] of Object.entries(theme.colors)) {
    const c = normalizeColor(value)
    if (c) colors[id] = c
  }
  // Later rules win over earlier ones for the same scope, as in VS Code.
  const byScope = new Map<string, Rule>()
  let global: Rule = {}
  for (const t of theme.tokenColors) {
    const rule = ruleOf(t.settings)
    if (!rule) continue
    if (t.scope === undefined || (typeof t.scope === 'string' && !t.scope.trim())) {
      // A rule for no scope (a .tmTheme's first): the text's own colours.
      global = { ...global, ...rule }
      continue
    }
    for (const s of scopesOf(t.scope)) byScope.set(s, { ...byScope.get(s), ...rule })
  }
  // A scope's rule as Monaco matches it: the longest scope named that it starts with.
  const matching = (scope: string): Rule | undefined => {
    for (let s = scope; s; s = s.includes('.') ? s.slice(0, s.lastIndexOf('.')) : '') {
      const r = byScope.get(s)
      if (r) return r
    }
    return undefined
  }
  const named = new Map<string, Rule>()
  for (const [scope, token] of monarchNames) {
    if (byScope.has(token) || named.has(token)) continue
    const r = matching(scope)
    if (r) named.set(token, r)
  }
  const fg = normalizeColor(colors['editor.foreground'])
  const bg = normalizeColor(colors['editor.background'])
  const text: Rule = { ...(fg ? { foreground: fg.slice(1, 7) } : {}), ...(bg ? { background: bg.slice(1, 7) } : {}), ...global }
  const rules: Monaco.editor.ITokenThemeRule[] = [
    ...(Object.keys(text).length ? [{ token: '', ...text }] : []),
    ...[...byScope].map(([token, r]) => ({ token, ...r })),
    ...[...named].map(([token, r]) => ({ token, ...r })),
  ]
  if (!colors['editor.foreground'] && global.foreground) colors['editor.foreground'] = `#${global.foreground}`
  if (!colors['editor.background'] && global.background) colors['editor.background'] = `#${global.background}`
  return { base: baseOf(uiTheme), inherit: true, rules, colors }
}

/** The page's own colours (its CSS variables) a theme's workbench colours give, the first of each list that it sets. */
const pageColorIds: [variable: string, ids: string[]][] = [
  ['--background', ['editor.background']],
  ['--foreground', ['foreground', 'editor.foreground']],
  ['--card', ['editorWidget.background', 'sideBar.background']],
  ['--card-foreground', ['editorWidget.foreground', 'foreground', 'editor.foreground']],
  ['--popover', ['quickInput.background', 'editorWidget.background', 'dropdown.background']],
  ['--popover-foreground', ['quickInput.foreground', 'editorWidget.foreground', 'foreground']],
  ['--primary', ['button.background', 'focusBorder']],
  ['--primary-foreground', ['button.foreground']],
  ['--primary-hover', ['button.hoverBackground']],
  ['--secondary', ['button.secondaryBackground']],
  ['--secondary-foreground', ['button.secondaryForeground']],
  ['--muted', ['input.background', 'editorWidget.background']],
  ['--muted-foreground', ['descriptionForeground', 'sideBarSectionHeader.foreground']],
  ['--accent', ['list.hoverBackground', 'list.inactiveSelectionBackground']],
  ['--accent-foreground', ['list.hoverForeground', 'list.activeSelectionForeground', 'foreground']],
  ['--border', ['panel.border', 'editorGroup.border', 'sideBar.border', 'widget.border']],
  ['--input', ['input.border', 'dropdown.border', 'input.background']],
  ['--ring', ['focusBorder']],
  ['--sidebar', ['sideBar.background']],
  ['--sidebar-foreground', ['sideBar.foreground', 'foreground']],
  ['--sidebar-accent', ['list.hoverBackground']],
  ['--sidebar-border', ['sideBar.border', 'panel.border']],
]

/** The page's CSS variables a theme sets, by name: only those whose colours it has. */
export function pageColors(colors: Record<string, string>): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [variable, ids] of pageColorIds) {
    for (const id of ids) {
      const c = normalizeColor(colors[id])
      if (c) {
        out[variable] = c
        break
      }
    }
  }
  return out
}

/** The page's CSS variables a theme can set (to take them all away again). */
export const pageColorVariables = pageColorIds.map(([v]) => v)

/** xterm.js's colours from a theme's terminal colours: only those it sets. */
export function terminalColors(colors: Record<string, string>): Record<string, string> {
  const ids: Record<string, string> = {
    background: 'terminal.background',
    foreground: 'terminal.foreground',
    cursor: 'terminalCursor.foreground',
    cursorAccent: 'terminalCursor.background',
    selectionBackground: 'terminal.selectionBackground',
    black: 'terminal.ansiBlack',
    red: 'terminal.ansiRed',
    green: 'terminal.ansiGreen',
    yellow: 'terminal.ansiYellow',
    blue: 'terminal.ansiBlue',
    magenta: 'terminal.ansiMagenta',
    cyan: 'terminal.ansiCyan',
    white: 'terminal.ansiWhite',
    brightBlack: 'terminal.ansiBrightBlack',
    brightRed: 'terminal.ansiBrightRed',
    brightGreen: 'terminal.ansiBrightGreen',
    brightYellow: 'terminal.ansiBrightYellow',
    brightBlue: 'terminal.ansiBrightBlue',
    brightMagenta: 'terminal.ansiBrightMagenta',
    brightCyan: 'terminal.ansiBrightCyan',
    brightWhite: 'terminal.ansiBrightWhite',
  }
  const out: Record<string, string> = {}
  for (const [key, id] of Object.entries(ids)) {
    const c = normalizeColor(colors[id])
    if (c) out[key] = c
  }
  // VS Code's terminal takes the panel's (the editor's) colours when the theme gives it none of its own.
  out.background ??= normalizeColor(colors['panel.background']) ?? normalizeColor(colors['editor.background']) ?? ''
  out.foreground ??= normalizeColor(colors['editor.foreground']) ?? ''
  for (const k of ['background', 'foreground']) if (!out[k]) delete out[k]
  return out
}
