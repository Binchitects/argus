/**
 * The colour themes: Arena's own (the default) and the named ones, each with a light and a dark mode. Their colours
 * are CSS (styles/index.css, under <html data-color-theme>); here are their names, and as hex the few colours what
 * takes no CSS needs: Code Arena's editor and terminal, and the swatches of the pickers. themes.test.ts checks
 * them against the CSS, and the list against the server's (Llm.Api/Identity/ColorThemes.cs).
 */
export const colorThemeIds = ['default', 'sakura', 'midnight', 'copper', 'forest', 'ocean', 'lavender'] as const

export type ColorThemeId = (typeof colorThemeIds)[number]

/**
 * A theme's colours in one mode: the CSS variables of the same names (primaryInk is --primary-ink), and the editor's
 * line numbers (3:1 on the background).
 */
export interface Palette {
  background: string
  foreground: string
  card: string
  muted: string
  border: string
  primary: string
  primaryInk: string
  primaryForeground: string
  lineNumber: string
}

export interface ColorThemeInfo {
  id: ColorThemeId
  label: string
  description: string
  light: Palette
  dark: Palette
}

export const colorThemes: ColorThemeInfo[] = [
  {
    id: 'default',
    label: 'Arena',
    description: 'Cool grey and blue: the default.',
    light: { background: '#fbfcfe', foreground: '#12161d', card: '#ffffff', muted: '#eff2f5', border: '#dfe3e8', primary: '#2474cf', primaryInk: '#0859ac', primaryForeground: '#fcfcfc', lineNumber: '#8b929c' },
    dark: { background: '#0c0e13', foreground: '#e9ebee', card: '#13161c', muted: '#1c1f24', border: '#282c31', primary: '#4c94ec', primaryInk: '#92c1fd', primaryForeground: '#080d16', lineNumber: '#5d646e' },
  },
  {
    id: 'sakura',
    label: 'Sakura',
    description: 'Blossom pink on calm, faintly rosy greys; plum-grey by night.',
    light: { background: '#fefcfc', foreground: '#1b1317', card: '#ffffff', muted: '#f5f0f2', border: '#e8e1e3', primary: '#ba4374', primaryInk: '#982b5a', primaryForeground: '#fcfcfc', lineNumber: '#9a8d92' },
    dark: { background: '#120d0f', foreground: '#eeeaeb', card: '#1a1417', muted: '#231d20', border: '#30292d', primary: '#d76797', primaryInk: '#f2a3c1', primaryForeground: '#140a0e', lineNumber: '#6c5f65' },
  },
  {
    id: 'midnight',
    label: 'Midnight',
    description: 'Indigo on cool greys; navy-grey by night.',
    light: { background: '#fcfcfe', foreground: '#13161d', card: '#ffffff', muted: '#f0f2f5', border: '#e1e3e8', primary: '#5569d1', primaryInk: '#3f50ad', primaryForeground: '#fcfcfc', lineNumber: '#8d919c' },
    dark: { background: '#0c0e13', foreground: '#e9ebef', card: '#13161c', muted: '#1d1f24', border: '#282b31', primary: '#7e86ec', primaryInk: '#aeb8fe', primaryForeground: '#0a0d16', lineNumber: '#5e636e' },
  },
  {
    id: 'copper',
    label: 'Copper',
    description: 'Burnt orange on warm greys; warm charcoal by night.',
    light: { background: '#fdfcfb', foreground: '#1c1410', card: '#ffffff', muted: '#f4f1ee', border: '#e7e2dd', primary: '#bc4d03', primaryInk: '#943c02', primaryForeground: '#fcfcfc', lineNumber: '#9a8f88' },
    dark: { background: '#120d0a', foreground: '#edebe7', card: '#1a1511', muted: '#231e1b', border: '#302a26', primary: '#d67523', primaryInk: '#eeae7b', primaryForeground: '#140b06', lineNumber: '#6b6259' },
  },
  {
    id: 'forest',
    label: 'Forest',
    description: 'Pine green on faintly sage greys; fern on green-grey by night.',
    light: { background: '#fcfdfb', foreground: '#101814', card: '#ffffff', muted: '#f0f2ef', border: '#e1e4df', primary: '#008542', primaryInk: '#026d36', primaryForeground: '#fcfcfc', lineNumber: '#89938b' },
    dark: { background: '#0b100d', foreground: '#eaece8', card: '#111814', muted: '#1b211e', border: '#262d29', primary: '#3eab5e', primaryInk: '#91d099', primaryForeground: '#06100a', lineNumber: '#5d665d' },
  },
  {
    id: 'ocean',
    label: 'Ocean',
    description: 'Deep teal on faintly sea-blue greys; aqua by night.',
    light: { background: '#fbfdfd', foreground: '#0f171c', card: '#ffffff', muted: '#eef3f4', border: '#dde4e6', primary: '#007e90', primaryInk: '#026677', primaryForeground: '#fcfcfc', lineNumber: '#879499' },
    dark: { background: '#0a0f12', foreground: '#e7ecec', card: '#10171a', muted: '#1a2023', border: '#252d30', primary: '#05a7a7', primaryInk: '#66d2ce', primaryForeground: '#040f14', lineNumber: '#586669' },
  },
  {
    id: 'lavender',
    label: 'Lavender',
    description: 'Violet on faintly lilac greys; lavender on twilight grey by night.',
    light: { background: '#fdfcfd', foreground: '#17151c', card: '#ffffff', muted: '#f2f1f4', border: '#e4e2e7', primary: '#7d5cc7', primaryInk: '#6343a4', primaryForeground: '#fcfcfc', lineNumber: '#938f9b' },
    dark: { background: '#0f0d12', foreground: '#eceaee', card: '#17151b', muted: '#201e23', border: '#2c2a31', primary: '#a17adf', primaryInk: '#c9aff4', primaryForeground: '#0f0b15', lineNumber: '#65616d' },
  },
]

export const isColorTheme = (v: unknown): v is ColorThemeId => typeof v === 'string' && (colorThemeIds as readonly string[]).includes(v)

export const colorThemeOf = (id: ColorThemeId): ColorThemeInfo => colorThemes.find((t) => t.id === id)!
