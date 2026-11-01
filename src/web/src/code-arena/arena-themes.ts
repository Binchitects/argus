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
 */
export function arenaMonacoThemes(): [string, editor.IStandaloneThemeData][] {
  return colorThemes.flatMap((theme) =>
    (['light', 'dark'] as const).map((kind): [string, editor.IStandaloneThemeData] => {
      const p = theme[kind]
      const light = kind === 'light'
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
            'editor.lineHighlightBackground': `${p.muted}80`,
            'editor.selectionBackground': `${p.primary}${light ? '33' : '40'}`,
            'editor.inactiveSelectionBackground': `${p.primary}${light ? '1f' : '26'}`,
            'editorCursor.foreground': p.primary,
            'editorWidget.background': p.card,
            'editorWidget.border': p.border,
            'editorGutter.background': p.background,
            'focusBorder': `${p.primary}${light ? '8c' : '99'}`,
            ...diffColors[kind],
          },
        },
      ]
    }),
  )
}
