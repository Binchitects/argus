import { describe, expect, it } from 'vitest'
import { colorThemeIds } from '@/lib/color-themes'
import { arenaMonacoThemes } from './arena-themes'
import { colorThemes } from './settings-schema'

describe("Monaco's themes for the app's colour themes", () => {
  const themes = new Map(arenaMonacoThemes())

  it('has one for each colour theme Code Arena offers, light and dark', () => {
    expect(themes.size).toBe(colorThemeIds.length * 2)
    for (const t of colorThemes.filter((c) => c.family)) expect(themes.get(t.monaco)?.base, t.name).toBe(t.kind === 'light' ? 'vs' : 'vs-dark')
  })

  it("Arena's own are as they were drawn by hand: the page's background, text, cards, border and blue", () => {
    expect(themes.get('arena-light')!.colors).toEqual({
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
})
