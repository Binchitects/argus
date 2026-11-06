import type { editor } from 'monaco-editor'
import { colorThemes, type Syntax } from '@/lib/color-themes'
import { arenaMonacoTheme } from './settings-schema'

// What an added or removed line looks like in a compare, in either mode: the status colours, the same in every colour theme.
const diffColors = {
  light: {
    'diffEditor.insertedTextBackground': '#00885624',
    'diffEditor.removedTextBackground': '#cc272e24',
    'diffEditor.insertedLineBackground': '#00885614',
    'diffEditor.removedLineBackground': '#cc272e14',
  },
  dark: {
    'diffEditor.insertedTextBackground': '#44b78230',
    'diffEditor.removedTextBackground': '#ed535030',
    'diffEditor.insertedLineBackground': '#44b7821a',
    'diffEditor.removedLineBackground': '#ed53501a',
  },
}

// VS Code's code colours ('vs' and 'vs-dark' give them) that fall under 4.5:1 on some theme's background or on the
// cursor's line, and its bracket pair colours (the 4th to 6th are transparent: not used). Each is made just as much
// darker (light) or lighter (dark) as it needs to read, so code looks as it does in VS Code.
const vsCodeTokens = {
  light: {
    'number': '#098658', 'attribute.value.number': '#098658', 'attribute.value.unit': '#098658', 'type': '#008080',
    'attribute.name': '#ff0000', 'metatag.content.html': '#ff0000', 'string.sql': '#ff0000', 'annotation': '#808080',
    'metatag.html': '#808080', 'metatag.xml': '#808080', 'operator.sql': '#778899', 'tag.id.pug': '#4f76ac', 'tag.class.pug': '#4f76ac',
    'invalid': '#cd3131',
  },
  dark: {
    'variable.predefined': '#4864aa', 'tag.id.pug': '#4f76ac', 'tag.class.pug': '#4f76ac', 'regexp': '#b46695', 'string.sql': '#ff0000',
    // Under 4.5:1 only on the lighter grounds of VS Code's themes (Dark+'s #1e1e1e, Solarized's, Nord's).
    'invalid': '#f44747', 'annotation': '#cc6666', 'delimiter.html': '#808080', 'delimiter.xml': '#808080', 'operator.swift': '#909090', 'operator.scss': '#909090',
    'metatag.html': '#569cd6', 'metatag.xml': '#569cd6', 'keyword.flow.scss': '#569cd6', 'meta.tag': '#ce9178', 'meta.scss': '#a79873',
  },
}
const vsCodeBrackets = { light: ['#0431fa', '#319331', '#7b3814'], dark: ['#ffd700', '#da70d6', '#179fff'] }

/** Monaco's tokens (its grammars' and the language servers' semantic ones) each of a VS Code theme's code colours paints. */
const syntaxTokens: Record<keyof Syntax, string[]> = {
  comment: ['comment', 'comment.doc', 'comment.content'],
  keyword: ['keyword', 'keyword.json', 'storage', 'tag', 'metatag', 'operator.sql', 'predefined.sql'],
  control: ['keyword.flow', 'keyword.control', 'keyword.return'],
  string: ['string', 'string.key.json', 'string.value.json', 'attribute.value', 'attribute.value.html', 'regexp', 'string.escape'],
  number: ['number', 'number.hex', 'number.float', 'number.binary', 'number.octal', 'constant', 'enumMember', 'attribute.value.number', 'attribute.value.unit'],
  function: ['function', 'method', 'macro', 'attribute.name', 'attribute.name.html'],
  type: ['type', 'type.identifier', 'class', 'interface', 'enum', 'struct', 'typeParameter', 'namespace', 'tag.id.pug', 'tag.class.pug'],
  variable: ['variable', 'variable.predefined', 'parameter', 'property'],
}

const rgb = (hex: string) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16))
const hexOf = (c: number[]) => `#${c.map((x) => Math.round(x).toString(16).padStart(2, '0')).join('')}`
function luminance(c: number[]) {
  const [r, g, b] = c.map((x) => (x / 255 <= 0.04045 ? x / 255 / 12.92 : ((x / 255 + 0.055) / 1.055) ** 2.4))
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!
}
const contrast = (a: number[], b: number[]) => {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p)
  return (x! + 0.05) / (y! + 0.05)
}

/** A colour of code, 4.5:1 on each of `grounds`: moved towards black (light) or white (dark) in small steps until it is. */
function readable(hex: string, grounds: number[][], light: boolean) {
  const from = rgb(hex)
  for (let t = 0; t <= 1; t += 0.02) {
    const c = from.map((x) => (light ? x * (1 - t) : x + (255 - x) * t))
    if (grounds.every((g) => contrast(c.map(Math.round), g) >= 4.5)) return hexOf(c)
  }
  return light ? '#000000' : '#ffffff'
}

/**
 * Monaco's theme for each of the app's colour themes, light and dark, by its name (arena-dark, arena-sakura-light...):
 * the page's colours (lib/color-themes.ts, src/styles/index.css as sRGB, since Monaco takes hex) over VS Code's tokens.
 * Every colour VS Code makes blue is set too, else 'vs' and 'vs-dark' would give it: the suggestions and lists, links,
 * buttons, the find widget's options, the peek view and the overview ruler.
 */
export function arenaMonacoThemes(): [string, editor.IStandaloneThemeData][] {
  return colorThemes.flatMap((theme) =>
    (['light', 'dark'] as const).map((kind): [string, editor.IStandaloneThemeData] => {
      const p = theme[kind]
      const light = kind === 'light'
      // A selected row (a suggestion, a list's, the palette's): a tint of the accent, as the page's selected items.
      const row = `${p.primary}${light ? '1f' : '26'}`
      // Code is read on the background and on the cursor's line (the muted colour at 80, half, over it).
      const back = rgb(p.background)
      const grounds = [back, rgb(p.muted).map((x, i) => back[i]! + ((x - back[i]!) * 0x80) / 255)]
      const code = (hex: string) => readable(hex, grounds, light)
      // A VS Code theme's own code colours (High Contrast on Monaco's high contrast, with its outlines); Arena's take VS Code's defaults.
      const syntax = theme.vscode?.syntax[kind]
      const own = syntax ? Object.entries(syntaxTokens).flatMap(([part, tokens]) => tokens.map((token) => [token, syntax[part as keyof Syntax]] as const)) : []
      return [
        arenaMonacoTheme(theme.id, kind),
        {
          base: theme.id === 'contrast' ? (light ? 'hc-light' : 'hc-black') : light ? 'vs' : 'vs-dark',
          inherit: true,
          rules: [...Object.entries(vsCodeTokens[kind]).filter(([token]) => !own.some(([t]) => t === token)), ...own].map(([token, fg]) => ({ token, foreground: code(fg).slice(1) })),
          colors: {
            ...Object.fromEntries(vsCodeBrackets[kind].map((fg, i) => [`editorBracketHighlight.foreground${i + 1}`, code(fg)])),
            'editor.background': p.background,
            'editor.foreground': p.foreground,
            'editorLineNumber.foreground': p.lineNumber,
            'editorLineNumber.activeForeground': p.foreground,
            // The last line's number, of a file that ends in a newline: Monaco's own is the others' at 40%, under 4.5:1.
            'editorLineNumber.dimmedForeground': p.lineNumber,
            'editor.lineHighlightBackground': `${p.muted}80`,
            'editor.selectionBackground': `${p.primary}${light ? '33' : '40'}`,
            'editor.inactiveSelectionBackground': `${p.primary}${light ? '1f' : '26'}`,
            'editor.wordHighlightStrongBackground': `${p.primary}${light ? '26' : '33'}`,
            'editorCursor.foreground': p.primary,
            'editorWidget.background': p.card,
            'editorWidget.border': p.border,
            'editorGutter.background': p.background,
            'focusBorder': `${p.primary}${light ? '8c' : '99'}`,
            'editorSuggestWidget.background': p.card,
            'editorSuggestWidget.border': p.border,
            'editorSuggestWidget.selectedBackground': row,
            'editorSuggestWidget.selectedForeground': p.foreground,
            // The letters typed, in each suggestion: the accent's ink, 4.5:1 on the row's tint.
            'editorSuggestWidget.highlightForeground': p.primaryInk,
            'editorSuggestWidget.focusHighlightForeground': p.primaryInk,
            'list.activeSelectionBackground': row,
            'list.activeSelectionForeground': p.foreground,
            'list.inactiveSelectionBackground': p.muted,
            'list.focusBackground': row,
            'list.focusForeground': p.foreground,
            'list.hoverBackground': p.muted,
            'list.highlightForeground': p.primaryInk,
            'list.focusHighlightForeground': p.primaryInk,
            'quickInputList.focusBackground': row,
            'quickInputList.focusForeground': p.foreground,
            'pickerGroup.foreground': p.primaryInk,
            'textLink.foreground': p.primaryInk,
            'textLink.activeForeground': p.primaryInk,
            'editorLink.activeForeground': p.primaryInk,
            'textBlockQuote.border': `${p.primary}80`,
            'button.background': p.primary,
            'button.foreground': p.primaryForeground,
            'progressBar.background': p.primary,
            'inputOption.activeBorder': p.primary,
            // Information is the accent, as on the page (--info): its squiggle, its mark in the overview ruler, the peek's border.
            'editorInfo.foreground': p.primary,
            'editorLightBulbAutoFix.foreground': p.primary,
            'editorOverviewRuler.rangeHighlightForeground': `${p.primary}99`,
            'peekView.border': p.primary,
            'peekViewTitle.background': p.muted,
            'peekViewResult.background': p.muted,
            'peekViewResult.selectionBackground': row,
            'peekViewResult.selectionForeground': p.foreground,
            'peekViewEditor.background': p.card,
            ...diffColors[kind],
          },
        },
      ]
    }),
  )
}
