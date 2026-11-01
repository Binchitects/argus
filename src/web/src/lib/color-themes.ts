/**
 * The colour themes: Arena's own (the default) and the named ones, each with a light and a dark mode. Their colours
 * are CSS (styles/index.css, under <html data-color-theme>); here are their names, and as hex the few colours what
 * takes no CSS needs: Code Arena's editor and terminal, and the swatches of the pickers. themes.test.ts checks
 * them against the CSS, and the list against the server's (Llm.Api/Identity/ColorThemes.cs).
 */
export const colorThemeIds = ['default', 'sakura', 'midnight', 'copper', 'forest', 'ocean', 'lavender'] as const

export type ColorThemeId = (typeof colorThemeIds)[number]

/** A theme's colours in one mode: the CSS variables of the same names, and the editor's line numbers (3:1 on the background). */
export interface Palette {
  background: string
  foreground: string
  card: string
  muted: string
  border: string
  primary: string
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
    light: { background: '#fbfcfe', foreground: '#12161d', card: '#ffffff', muted: '#eff2f5', border: '#dfe3e8', primary: '#2474cf', lineNumber: '#8b929c' },
    dark: { background: '#0c0e13', foreground: '#e9ebee', card: '#13161c', muted: '#1c1f24', border: '#282c31', primary: '#4c94ec', lineNumber: '#5d646e' },
  },
  {
    id: 'sakura',
    label: 'Sakura',
    description: 'Blossom pink on blush paper; lantern-lit plum by night.',
    light: { background: '#fff8fa', foreground: '#2c1822', card: '#fffdfe', muted: '#faeff3', border: '#eedce2', primary: '#c14177', lineNumber: '#9c868f' },
    dark: { background: '#150c11', foreground: '#f2eaee', card: '#1e1419', muted: '#281d23', border: '#372931', primary: '#ed90b5', lineNumber: '#705f67' },
  },
  {
    id: 'midnight',
    label: 'Midnight',
    description: 'Deep indigo on cool slate; a navy night sky by night.',
    light: { background: '#f9fafe', foreground: '#10172d', card: '#fefeff', muted: '#eff1f8', border: '#dcdfeb', primary: '#3c49cc', lineNumber: '#868c9d' },
    dark: { background: '#060b1b', foreground: '#e4eaf4', card: '#0c1426', muted: '#161e2f', border: '#222b3f', primary: '#8b94f4', lineNumber: '#5a6376' },
  },
  {
    id: 'copper',
    label: 'Copper',
    description: 'Burnt orange on warm limestone; a forge’s glow on charcoal by night.',
    light: { background: '#fcf9f5', foreground: '#271d17', card: '#fffdfb', muted: '#f4efea', border: '#e5dcd5', primary: '#b64e10', lineNumber: '#958a82' },
    dark: { background: '#140e0a', foreground: '#f0eae3', card: '#1c1611', muted: '#261f1a', border: '#342c26', primary: '#e78a45', lineNumber: '#6d6259' },
  },
  {
    id: 'forest',
    label: 'Forest',
    description: 'Pine green on pale sage; fern in evergreen shade by night.',
    light: { background: '#f8fbf6', foreground: '#102118', card: '#fdfffd', muted: '#eef2ec', border: '#dbe2d8', primary: '#227240', lineNumber: '#828e83' },
    dark: { background: '#08110c', foreground: '#e8ede5', card: '#0f1913', muted: '#18221c', border: '#243029', primary: '#69c27e', lineNumber: '#5a665b' },
  },
  {
    id: 'ocean',
    label: 'Ocean',
    description: 'Deep teal on sea glass; aqua over the deep by night.',
    light: { background: '#f4fbfc', foreground: '#0a1f2b', card: '#fcffff', muted: '#e9f3f5', border: '#d3e3e6', primary: '#0c7686', lineNumber: '#7c9097' },
    dark: { background: '#030f16', foreground: '#e2eeee', card: '#071820', muted: '#10222a', border: '#1c3038', primary: '#31c8c9', lineNumber: '#50676d' },
  },
  {
    id: 'lavender',
    label: 'Lavender',
    description: 'Violet on lilac white; lavender in the twilight by night.',
    light: { background: '#fcf9ff', foreground: '#20182d', card: '#fefdff', muted: '#f3f0f8', border: '#e4deec', primary: '#7447c8', lineNumber: '#8f899c' },
    dark: { background: '#100c16', foreground: '#eeebf3', card: '#181320', muted: '#211c2a', border: '#2f2939', primary: '#b897f0', lineNumber: '#676173' },
  },
]

export const isColorTheme = (v: unknown): v is ColorThemeId => typeof v === 'string' && (colorThemeIds as readonly string[]).includes(v)

export const colorThemeOf = (id: ColorThemeId): ColorThemeInfo => colorThemes.find((t) => t.id === id)!
