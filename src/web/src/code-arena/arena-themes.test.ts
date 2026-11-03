import { describe, expect, it } from 'vitest'
import { colorThemeIds, colorThemes as appColorThemes } from '@/lib/color-themes'
import { arenaMonacoThemes } from './arena-themes'
import { arenaMonacoTheme, colorThemes } from './settings-schema'

/** WCAG's contrast of two hex colours; one with an alpha (#rrggbbaa) is laid over the other, or over `ground`. */
function contrast(fg: string, bg: string, ground?: string) {
  const rgb = (h: string) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255)
  const over = (h: string, under: number[]) => {
    const a = h.length === 9 ? parseInt(h.slice(7), 16) / 255 : 1
    return rgb(h).map((c, i) => c * a + under[i]! * (1 - a))
  }
  const back = ground ? over(bg, rgb(ground)) : rgb(bg)
  const lum = (c: number[]) => {
    const [r, g, b] = c.map((x) => (x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4))
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!
  }
  const [x, y] = [lum(over(fg, back)), lum(back)].sort((p, q) => q - p)
  return (x! + 0.05) / (y! + 0.05)
}

// VS Code's own blues, which Monaco's 'vs' and 'vs-dark' give a widget the theme leaves alone: the suggestion and list
// rows, their matched letters, links, buttons, the progress bar, the find widget's options, the overview ruler's marks.
const vsCodeBlues = ['#0060c0', '#04395e', '#0066bf', '#2aaaff', '#3794ff', '#006ab1', '#4e94ce', '#007acc', '#0e639c', '#0e70c0', '#004972', '#0063d3', '#59a4f9', '#3399ff', '#001f33', '#f2f8fc', '#75beff']
// The colours with one of those blues, given the theme's own colours.
const bluesTaken = [
  'editorSuggestWidget.selectedBackground', 'editorSuggestWidget.highlightForeground', 'editorSuggestWidget.focusHighlightForeground',
  'list.activeSelectionBackground', 'list.highlightForeground', 'list.focusHighlightForeground', 'quickInputList.focusBackground', 'pickerGroup.foreground',
  'textLink.foreground', 'textLink.activeForeground', 'editorLink.activeForeground', 'textBlockQuote.border', 'button.background', 'progressBar.background',
  'inputOption.activeBorder', 'editorOverviewRuler.rangeHighlightForeground', 'editor.wordHighlightStrongBackground', 'editorInfo.foreground',
  'editorLightBulbAutoFix.foreground', 'peekView.border', 'peekViewEditor.background', 'peekViewResult.selectionBackground',
]

describe("Monaco's themes for the app's colour themes", () => {
  const themes = new Map(arenaMonacoThemes())
  const every = appColorThemes.flatMap((t) => (['light', 'dark'] as const).map((kind) => ({ id: t.id, kind, palette: t[kind], theme: themes.get(arenaMonacoTheme(t.id, kind))! })))

  it('has one for each colour theme Code Arena offers, light and dark', () => {
    expect(themes.size).toBe(colorThemeIds.length * 2)
    for (const t of colorThemes.filter((c) => c.family)) expect(themes.get(t.monaco)?.base, t.name).toBe(t.kind === 'light' ? 'vs' : 'vs-dark')
  })

  it("Arena's own are as they were drawn by hand: the page's background, text, cards, border and blue", () => {
    expect(themes.get('arena-light')!.colors).toMatchObject({
      'editor.background': '#fbfcfe',
      'editor.foreground': '#12161d',
      'editorLineNumber.foreground': '#8b929c',
      'editorLineNumber.activeForeground': '#12161d',
      'editor.lineHighlightBackground': '#eff2f580',
      'editor.selectionBackground': '#2474cf33',
      'editor.inactiveSelectionBackground': '#2474cf1f',
      'editorCursor.foreground': '#2474cf',
      'editorWidget.background': '#ffffff',
      'editorWidget.border': '#dfe3e8',
      'editorGutter.background': '#fbfcfe',
      'focusBorder': '#2474cf8c',
      'diffEditor.insertedTextBackground': '#00885624',
      'diffEditor.removedTextBackground': '#cc272e24',
      'diffEditor.insertedLineBackground': '#00885614',
      'diffEditor.removedLineBackground': '#cc272e14',
    })
    expect(themes.get('arena-dark')!.colors).toMatchObject({ 'editor.background': '#0c0e13', 'editorWidget.background': '#13161c', 'editor.selectionBackground': '#4c94ec40', 'focusBorder': '#4c94ec99' })
  })

  it("a colour theme's are its own colours", () => {
    expect(themes.get('arena-sakura-dark')!.colors).toMatchObject({ 'editor.background': '#120d0f', 'editor.foreground': '#eeeaeb', 'editorCursor.foreground': '#d76797', 'editorWidget.background': '#1a1417' })
    expect(themes.get('arena-ocean-light')!.colors).toMatchObject({ 'editor.background': '#fbfdfd', 'editor.selectionBackground': '#007e9033', 'editorLineNumber.foreground': '#879499' })
  })

  it.each(every)("$id $kind: the suggestions, lists, links, buttons and the overview ruler wear its colours, not VS Code's blue", ({ kind, palette: p, theme }) => {
    const c = theme.colors
    const row = `${p.primary}${kind === 'light' ? '1f' : '26'}`
    expect(c).toMatchObject({
      'editorSuggestWidget.background': p.card,
      'editorSuggestWidget.border': p.border,
      'editorSuggestWidget.selectedBackground': row,
      'editorSuggestWidget.selectedForeground': p.foreground,
      'editorSuggestWidget.highlightForeground': p.primaryInk,
      'editorSuggestWidget.focusHighlightForeground': p.primaryInk,
      'list.activeSelectionBackground': row,
      'list.activeSelectionForeground': p.foreground,
      'list.focusBackground': row,
      'quickInputList.focusBackground': row,
      'list.highlightForeground': p.primaryInk,
      'textLink.foreground': p.primaryInk,
      'button.background': p.primary,
      'button.foreground': p.primaryForeground,
      'progressBar.background': p.primary,
      'inputOption.activeBorder': p.primary,
    })
    expect(bluesTaken.filter((k) => !c[k]), 'left to VS Code').toEqual([])
    const blue = Object.entries(c).filter(([, v]) => vsCodeBlues.includes(v.slice(0, 7).toLowerCase()))
    expect(blue, "VS Code's blue").toEqual([])
  })

  it.each(every)('$id $kind: text in the editor\'s widgets is 4.5:1', ({ theme }) => {
    const c = theme.colors
    const low: string[] = []
    const check = (fg: string, bg: string, ground?: string) => {
      const r = contrast(c[fg]!, c[bg]!, ground && c[ground])
      if (r < 4.5) low.push(`${fg} on ${bg}${ground ? ` over ${ground}` : ''}: ${r.toFixed(2)}`)
    }
    check('editorSuggestWidget.highlightForeground', 'editorSuggestWidget.background')
    check('editorSuggestWidget.selectedForeground', 'editorSuggestWidget.selectedBackground', 'editorSuggestWidget.background')
    check('editorSuggestWidget.focusHighlightForeground', 'editorSuggestWidget.selectedBackground', 'editorSuggestWidget.background')
    check('list.activeSelectionForeground', 'list.activeSelectionBackground', 'editorWidget.background')
    check('textLink.foreground', 'editorWidget.background')
    check('pickerGroup.foreground', 'editorWidget.background')
    check('button.foreground', 'button.background')
    expect(low).toEqual([])
  })
})
