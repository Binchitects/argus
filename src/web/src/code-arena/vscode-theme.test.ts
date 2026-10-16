import { describe, expect, it } from 'vitest'
import { loadColorTheme, normalizeColor, pageColors, resolvePath, terminalColors, toMonacoTheme } from './vscode-theme'

describe("VS Code's colour themes as Monaco's", () => {
  it('reads a theme with what it includes (relative to each file), its own colours and rules over the included ones', async () => {
    const files: Record<string, string> = {
      'themes/night.json': `{
        // JSON with comments, as VS Code's themes are.
        "include": "./parts/base.json",
        "colors": { "editor.background": "#101020", "editor.lineHighlightBackground": null },
        "tokenColors": [{ "scope": "comment", "settings": { "foreground": "#808080" } }],
      }`,
      'themes/parts/base.json': '{ "include": "../../shared/root.json", "colors": { "editor.foreground": "#e0e0e0", "editor.background": "#000000" }, "tokenColors": "./rules.json" }',
      'themes/parts/rules.json': '{ "tokenColors": [{ "scope": "string", "settings": { "foreground": "#00ff00" } }] }',
      'shared/root.json': '{ "colors": { "editor.lineHighlightBackground": "#222222", "focusBorder": "#123456" } }',
    }
    const read: string[] = []
    const theme = await loadColorTheme('themes/night.json', async (path) => {
      read.push(path)
      if (!(path in files)) throw new Error(`no ${path}`)
      return files[path]!
    })
    expect(read).toEqual(['themes/night.json', 'themes/parts/base.json', 'shared/root.json', 'themes/parts/rules.json'])
    expect(theme.colors).toEqual({ 'editor.background': '#101020', 'editor.foreground': '#e0e0e0', focusBorder: '#123456' })
    expect(theme.tokenColors.map((t) => t.scope)).toEqual(['string', 'comment'])
  })

  it('stops at ten includes, and never leaves the extension', async () => {
    let reads = 0
    const theme = await loadColorTheme('a.json', async () => {
      reads++
      return '{ "include": "./a.json", "colors": { "x": "#fff" } }'
    })
    expect(reads).toBe(11)
    expect(theme.colors).toEqual({ x: '#fff' })
    expect(resolvePath('themes', '../../etc/passwd')).toBeNull()
    expect(resolvePath('themes', '/etc/passwd')).toBeNull()
    expect(resolvePath('themes', 'C:/x.json')).toBeNull()
    expect(resolvePath('themes/a', '..\\b.json')).toBe('themes/b.json')
  })

  it('gives Monaco rules by scope, colours of six hex digits, the font styles it knows, and Monarch names for the common scopes', () => {
    const monaco = toMonacoTheme(
      {
        colors: { 'editor.background': '#101020', 'editor.foreground': '#EEE', 'editorCursor.foreground': '#ff000080', 'not.a.colour': 'red', 'sideBar.background': '#abc' },
        tokenColors: [
          { settings: { foreground: '#cccccc', background: '#111111' } },
          { scope: 'comment', settings: { foreground: '#808080', fontStyle: 'italic' } },
          { scope: 'string, string.quoted', settings: { foreground: '#0f0' } },
          { scope: ['keyword', 'storage.type'], settings: { foreground: '#ff79c6', fontStyle: 'bold underline shiny' } },
          { scope: 'constant.numeric', settings: { foreground: '#bd93f980' } },
          { scope: 'entity.name.type.class', settings: { foreground: '#8be9fd' } },
          { scope: 'source.js string', settings: { foreground: '#123456' } },
          { scope: 'keyword', settings: { fontStyle: '' } },
          { scope: 'invalid', settings: { foreground: 'not a colour' } },
        ],
      },
      'vs-dark',
    )
    expect(monaco.base).toBe('vs-dark')
    expect(monaco.inherit).toBe(true)
    // Monaco takes #rrggbb and #rrggbbaa: #EEE and #abc are spelled out, a name is left out.
    expect(monaco.colors).toEqual({ 'editor.background': '#101020', 'editor.foreground': '#eeeeee', 'editorCursor.foreground': '#ff000080', 'sideBar.background': '#aabbcc' })
    const rule = (token: string) => monaco.rules.find((r) => r.token === token)
    // The text's own colours: the theme's rule without a scope, over the editor's.
    expect(rule('')).toEqual({ token: '', foreground: 'cccccc', background: '111111' })
    expect(rule('comment')).toEqual({ token: 'comment', foreground: '808080', fontStyle: 'italic' })
    expect(rule('string.quoted')).toEqual({ token: 'string.quoted', foreground: '00ff00' })
    // A later rule for the same scope changes only what it sets.
    expect(rule('keyword')).toEqual({ token: 'keyword', foreground: 'ff79c6', fontStyle: '' })
    // Monarch's names: a number's colour (alpha dropped); a narrower scope's colour is not a broader one's (a class's is not every type's).
    expect(rule('number')).toEqual({ token: 'number', foreground: 'bd93f9' })
    expect(rule('type')).toBeUndefined()
    expect(rule('delimiter')).toBeUndefined()
    // A selector of several scopes cannot be Monaco's; a rule with no valid colour is none.
    expect(monaco.rules.some((r) => r.token.includes(' '))).toBe(false)
    expect(rule('invalid')).toBeUndefined()
  })

  it('names Monarch tokens from the broader scope a theme colours', () => {
    const monaco = toMonacoTheme({ colors: {}, tokenColors: [{ scope: 'entity.name.type', settings: { foreground: '#8be9fd' } }, { scope: 'entity.name.tag', settings: { foreground: '#ff5555' } }, { scope: 'punctuation', settings: { foreground: '#f8f8f2' } }] }, 'vs')
    expect(monaco.base).toBe('vs')
    expect(monaco.rules).toEqual(
      expect.arrayContaining([
        { token: 'type', foreground: '8be9fd' },
        { token: 'type.identifier', foreground: '8be9fd' },
        { token: 'tag', foreground: 'ff5555' },
        { token: 'delimiter', foreground: 'f8f8f2' },
      ]),
    )
    expect(toMonacoTheme({ colors: {}, tokenColors: [] }, 'hc-black').base).toBe('hc-black')
    expect(toMonacoTheme({ colors: {}, tokenColors: [] }, 'something else').base).toBe('vs-dark')
  })

  it("gives the page's CSS variables and the terminal's colours from the workbench's", () => {
    const colors = { 'editor.background': '#282a36', foreground: '#f8f8f2', 'sideBar.background': '#21222c', 'button.background': '#44475a', 'list.hoverBackground': '#44475a75', focusBorder: '#6272a4', 'terminal.ansiRed': '#ff5555', 'terminal.foreground': 'nope' }
    expect(pageColors(colors)).toEqual({
      '--background': '#282a36',
      '--foreground': '#f8f8f2',
      '--card': '#21222c',
      '--card-foreground': '#f8f8f2',
      '--popover-foreground': '#f8f8f2',
      '--primary': '#44475a',
      '--accent': '#44475a75',
      '--accent-foreground': '#f8f8f2',
      '--ring': '#6272a4',
      '--sidebar': '#21222c',
      '--sidebar-foreground': '#f8f8f2',
      '--sidebar-accent': '#44475a75',
    })
    // The terminal takes the editor's background when the theme gives it none of its own.
    expect(terminalColors(colors)).toEqual({ red: '#ff5555', background: '#282a36' })
    expect(normalizeColor('#AbC')).toBe('#aabbcc')
    expect(normalizeColor('#abcd')).toBe('#aabbccdd')
    expect(normalizeColor('rgb(1,2,3)')).toBeNull()
    expect(normalizeColor(5)).toBeNull()
  })
})
