import type { editor } from 'monaco-editor'
import { colorThemes } from '@/lib/color-themes'
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
      return [
        arenaMonacoTheme(theme.id, kind),
        {
          base: light ? 'vs' : 'vs-dark',
          inherit: true,
          rules: [],
          colors: {
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
