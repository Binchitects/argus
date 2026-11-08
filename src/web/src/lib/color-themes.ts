/**
 * The colour themes: Arena's own (the default), the calm named ones, and VS Code's most used, each with a light and a
 * dark mode. Their colours
 * are CSS (styles/index.css, under <html data-color-theme>); here are their names, and as hex the few colours what
 * takes no CSS needs: Code Arena's editor and terminal, and the swatches of the pickers. themes.test.ts checks
 * them against the CSS, and the list against the server's (Llm.Api/Identity/ColorThemes.cs).
 */
export const colorThemeIds = ['default', 'sakura', 'midnight', 'copper', 'forest', 'ocean', 'lavender', 'vscode', 'modern', 'monokai', 'solarized', 'onedark', 'dracula', 'github', 'nord', 'contrast'] as const

export type ColorThemeId = (typeof colorThemeIds)[number]

/**
 * A theme's colours in one mode: the CSS variables of the same names (primaryInk is --primary-ink), and the editor's
 * line numbers (4.5:1 on the background, and on a compare's added and removed lines, as text is).
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

/** The colours of code: VS Code's tokens of a theme (Code Arena's editor takes them, each made to read 4.5:1). */
export interface Syntax {
  comment: string
  keyword: string
  string: string
  number: string
  function: string
  type: string
  variable: string
  /** if, return, import: VS Code's keyword.control. */
  control: string
}

export interface ColorThemeInfo {
  id: ColorThemeId
  label: string
  description: string
  light: Palette
  dark: Palette
  /**
   * One of VS Code's themes: its names there for each mode (settings.json's "workbench.colorTheme"), and the colours of
   * its code. None: Arena's own, calm, with VS Code's default code colours.
   */
  vscode?: { names: { light: string; dark: string }; syntax: { light: Syntax; dark: Syntax } }
}

export const colorThemes: ColorThemeInfo[] = [
  {
    id: 'default',
    label: 'Arena',
    description: 'Cool grey and blue: the default.',
    light: { background: '#fbfcfe', foreground: '#12161d', card: '#ffffff', muted: '#eff2f5', border: '#dfe3e8', primary: '#2474cf', primaryInk: '#0859ac', primaryForeground: '#fcfcfc', lineNumber: '#666d77' },
    dark: { background: '#0c0e13', foreground: '#e9ebee', card: '#13161c', muted: '#1c1f24', border: '#282c31', primary: '#4c94ec', primaryInk: '#92c1fd', primaryForeground: '#080d16', lineNumber: '#7d858f' },
  },
  {
    id: 'sakura',
    label: 'Sakura',
    description: 'Blossom pink on calm greys; on plain grey by night.',
    light: { background: '#fcfcfc', foreground: '#171616', card: '#ffffff', muted: '#f2f1f2', border: '#e3e3e3', primary: '#ba4374', primaryInk: '#982b5a', primaryForeground: '#fcfcfc', lineNumber: '#6b6a6a' },
    dark: { background: '#0e0e0e', foreground: '#ebebeb', card: '#161616', muted: '#1f1f1f', border: '#2b2b2b', primary: '#d76797', primaryInk: '#f2a3c1', primaryForeground: '#140a0e', lineNumber: '#848484' },
  },
  {
    id: 'midnight',
    label: 'Midnight',
    description: 'Indigo on calm greys; on plain grey by night.',
    light: { background: '#fcfcfc', foreground: '#161617', card: '#ffffff', muted: '#f1f2f2', border: '#e3e3e3', primary: '#5569d1', primaryInk: '#3f50ad', primaryForeground: '#fcfcfc', lineNumber: '#6c6c6d' },
    dark: { background: '#0e0e0e', foreground: '#ebebeb', card: '#161616', muted: '#1f1f1f', border: '#2b2b2b', primary: '#7e86ec', primaryInk: '#aeb8fe', primaryForeground: '#0a0d16', lineNumber: '#848484' },
  },
  {
    id: 'copper',
    label: 'Copper',
    description: 'Burnt orange on calm greys; on plain grey by night.',
    light: { background: '#fcfcfc', foreground: '#171615', card: '#ffffff', muted: '#f2f2f1', border: '#e3e3e2', primary: '#bc4d03', primaryInk: '#943c02', primaryForeground: '#fcfcfc', lineNumber: '#6d6c6c' },
    dark: { background: '#0e0e0e', foreground: '#ebebeb', card: '#161616', muted: '#1f1f1f', border: '#2b2b2b', primary: '#d67523', primaryInk: '#eeae7b', primaryForeground: '#140b06', lineNumber: '#888888' },
  },
  {
    id: 'forest',
    label: 'Forest',
    description: 'Pine green on calm greys; fern on plain grey by night.',
    light: { background: '#fcfcfc', foreground: '#151616', card: '#ffffff', muted: '#f1f2f1', border: '#e3e3e2', primary: '#008542', primaryInk: '#026d36', primaryForeground: '#fcfcfc', lineNumber: '#6b6c6c' },
    dark: { background: '#0e0e0e', foreground: '#ebebeb', card: '#161616', muted: '#1f1f1f', border: '#2b2b2b', primary: '#3eab5e', primaryInk: '#91d099', primaryForeground: '#06100a', lineNumber: '#848484' },
  },
  {
    id: 'ocean',
    label: 'Ocean',
    description: 'Deep teal on calm greys; aqua on plain grey by night.',
    light: { background: '#fcfcfc', foreground: '#151617', card: '#ffffff', muted: '#f1f2f2', border: '#e2e3e3', primary: '#007e90', primaryInk: '#026677', primaryForeground: '#fcfcfc', lineNumber: '#6b6c6d' },
    dark: { background: '#0e0e0e', foreground: '#ebebeb', card: '#161616', muted: '#1f1f1f', border: '#2b2b2b', primary: '#05a7a7', primaryInk: '#66d2ce', primaryForeground: '#040f14', lineNumber: '#888888' },
  },
  {
    id: 'lavender',
    label: 'Lavender',
    description: 'Violet on calm greys; lavender on plain grey by night.',
    light: { background: '#fcfcfc', foreground: '#161617', card: '#ffffff', muted: '#f2f2f2', border: '#e3e3e3', primary: '#7d5cc7', primaryInk: '#6343a4', primaryForeground: '#fcfcfc', lineNumber: '#6d6c6d' },
    dark: { background: '#0e0e0e', foreground: '#ebebeb', card: '#161616', muted: '#1f1f1f', border: '#2b2b2b', primary: '#a17adf', primaryInk: '#c9aff4', primaryForeground: '#0f0b15', lineNumber: '#858585' },
  },  {
    id: 'vscode',
    label: 'VS Code',
    description: "VS Code's Dark+ and Light+: neutral greys, its blue.",
    light: { background: '#ffffff', foreground: '#000000', card: '#f3f3f3', muted: '#f3f3f3', border: '#e5e5e5', primary: '#0279c9', primaryInk: '#005e9f', primaryForeground: '#ffffff', lineNumber: '#1f7590' },
    dark: { background: '#1e1e1e', foreground: '#d4d4d4', card: '#252526', muted: '#2d2d2d', border: '#454545', primary: '#2875af', primaryInk: '#4e9fff', primaryForeground: '#ffffff', lineNumber: '#949494' },
    vscode: {
      names: { light: 'Default Light+', dark: 'Default Dark+' },
      syntax: {
        light: { comment: '#008000', keyword: '#0100ff', string: '#a31515', number: '#098658', function: '#795e26', type: '#267f99', variable: '#001080', control: '#af00db' },
        dark: { comment: '#6a9955', keyword: '#569cd6', string: '#ce9178', number: '#b5cea8', function: '#dcdcaa', type: '#4ec9b0', variable: '#9cdcfe', control: '#c586c0' },
      },
    },
  },
  {
    id: 'modern',
    label: 'VS Code Modern',
    description: "VS Code's Dark Modern and Light Modern: near black and white, a bright blue.",
    light: { background: '#ffffff', foreground: '#3b3b3b', card: '#f8f8f8', muted: '#f2f2f2', border: '#e5e5e5', primary: '#005fb8', primaryInk: '#005bb1', primaryForeground: '#ffffff', lineNumber: '#656d78' },
    dark: { background: '#1f1f1f', foreground: '#cccccc', card: '#202020', muted: '#2a2d2e', border: '#2b2b2b', primary: '#117edb', primaryInk: '#4daafc', primaryForeground: '#0d0d0d', lineNumber: '#8d95a1' },
    vscode: {
      names: { light: 'Default Light Modern', dark: 'Default Dark Modern' },
      syntax: {
        light: { comment: '#008000', keyword: '#0100ff', string: '#a31515', number: '#098658', function: '#795e26', type: '#267f99', variable: '#001080', control: '#af00db' },
        dark: { comment: '#6a9955', keyword: '#569cd6', string: '#ce9178', number: '#b5cea8', function: '#dcdcaa', type: '#4ec9b0', variable: '#9cdcfe', control: '#c586c0' },
      },
    },
  },
  {
    id: 'monokai',
    label: 'Monokai',
    description: "Monokai's warm charcoal and its bright pink, green and yellow; a light version by day.",
    light: { background: '#fafaf8', foreground: '#272822', card: '#ffffff', muted: '#efefea', border: '#d8d8d0', primary: '#c2185b', primaryInk: '#a31550', primaryForeground: '#ffffff', lineNumber: '#6e6a57' },
    dark: { background: '#272822', foreground: '#f8f8f2', card: '#1e1f1c', muted: '#3e3d32', border: '#414339', primary: '#66d9ef', primaryInk: '#66d9ef', primaryForeground: '#0d0e09', lineNumber: '#9e9e98' },
    vscode: {
      names: { light: 'Monokai Light', dark: 'Monokai' },
      syntax: {
        light: { comment: '#75715e', keyword: '#c2185b', string: '#8a7b00', number: '#7b4fd6', function: '#5a8a00', type: '#0087a3', variable: '#272822', control: '#c2185b' },
        dark: { comment: '#88846f', keyword: '#f92672', string: '#e6db74', number: '#ae81ff', function: '#a6e22e', type: '#66d9ef', variable: '#f8f8f2', control: '#f92672' },
      },
    },
  },
  {
    id: 'solarized',
    label: 'Solarized',
    description: "Solarized Dark and Light: Ethan Schoonover's blue-greens and cream.",
    light: { background: '#fdf6e3', foreground: '#3c5158', card: '#fffbf0', muted: '#eee8d5', border: '#ddd6c1', primary: '#047ac0', primaryInk: '#00629d', primaryForeground: '#ffffff', lineNumber: '#5d6a6a' },
    dark: { background: '#002b36', foreground: '#9eacac', card: '#00212b', muted: '#073642', border: '#0d4a58', primary: '#268bd2', primaryInk: '#4dacf5', primaryForeground: '#001118', lineNumber: '#8aa1a8' },
    vscode: {
      names: { light: 'Solarized Light', dark: 'Solarized Dark' },
      syntax: {
        light: { comment: '#93a1a1', keyword: '#859900', string: '#2aa198', number: '#d33682', function: '#268bd2', type: '#b58900', variable: '#268bd2', control: '#859900' },
        dark: { comment: '#586e75', keyword: '#859900', string: '#2aa198', number: '#d33682', function: '#268bd2', type: '#b58900', variable: '#268bd2', control: '#859900' },
      },
    },
  },
  {
    id: 'onedark',
    label: 'One Dark Pro',
    description: "Atom's One Dark (One Dark Pro) and One Light.",
    light: { background: '#fafafa', foreground: '#383a42', card: '#ffffff', muted: '#f0f0f1', border: '#dbdbdc', primary: '#366de6', primaryInk: '#2257ce', primaryForeground: '#ffffff', lineNumber: '#69696b' },
    dark: { background: '#282c34', foreground: '#abb2bf', card: '#21252b', muted: '#2c313a', border: '#3e4452', primary: '#4e7ace', primaryInk: '#61afef', primaryForeground: '#0a0d14', lineNumber: '#98a3bb' },
    vscode: {
      names: { light: 'One Light', dark: 'One Dark Pro' },
      syntax: {
        light: { comment: '#a0a1a7', keyword: '#a626a4', string: '#50a14f', number: '#986801', function: '#4078f2', type: '#c18401', variable: '#e45649', control: '#a626a4' },
        dark: { comment: '#7f848e', keyword: '#c678dd', string: '#98c379', number: '#d19a66', function: '#61afef', type: '#e5c07b', variable: '#e06c75', control: '#c678dd' },
      },
    },
  },
  {
    id: 'dracula',
    label: 'Dracula',
    description: "Dracula's purple night and its Alucard day.",
    light: { background: '#fffbeb', foreground: '#1f1f1f', card: '#ffffff', muted: '#f5f0dc', border: '#ded9c3', primary: '#644ac9', primaryInk: '#6147c6', primaryForeground: '#ffffff', lineNumber: '#635d97' },
    dark: { background: '#282a36', foreground: '#f8f8f2', card: '#21222c', muted: '#343746', border: '#3b3e4f', primary: '#bd93f9', primaryInk: '#c7a1ff', primaryForeground: '#0b0c17', lineNumber: '#8e9fd4' },
    vscode: {
      names: { light: 'Alucard', dark: 'Dracula' },
      syntax: {
        light: { comment: '#635d97', keyword: '#a3144d', string: '#846e15', number: '#644ac9', function: '#14710a', type: '#036a96', variable: '#1f1f1f', control: '#a3144d' },
        dark: { comment: '#6272a4', keyword: '#ff79c6', string: '#f1fa8c', number: '#bd93f9', function: '#50fa7b', type: '#8be9fd', variable: '#f8f8f2', control: '#ff79c6' },
      },
    },
  },
  {
    id: 'github',
    label: 'GitHub',
    description: "GitHub Dark and GitHub Light, as GitHub's own theme for VS Code.",
    light: { background: '#ffffff', foreground: '#1f2328', card: '#ffffff', muted: '#f6f8fa', border: '#d1d9e0', primary: '#0969da', primaryInk: '#005dc8', primaryForeground: '#ffffff', lineNumber: '#59636e' },
    dark: { background: '#0d1117', foreground: '#e6edf3', card: '#161b22', muted: '#161b22', border: '#30363d', primary: '#1f6feb', primaryInk: '#4695fa', primaryForeground: '#ffffff', lineNumber: '#818995' },
    vscode: {
      names: { light: 'GitHub Light', dark: 'GitHub Dark' },
      syntax: {
        light: { comment: '#59636e', keyword: '#cf222e', string: '#0a3069', number: '#0550ae', function: '#8250df', type: '#953800', variable: '#953800', control: '#cf222e' },
        dark: { comment: '#9198a1', keyword: '#ff7b72', string: '#a5d6ff', number: '#79c0ff', function: '#d2a8ff', type: '#ffa657', variable: '#ffa657', control: '#ff7b72' },
      },
    },
  },
  {
    id: 'nord',
    label: 'Nord',
    description: "Nord's arctic polar night and its snow storm by day.",
    light: { background: '#eceff4', foreground: '#2e3440', card: '#f5f7fa', muted: '#e5e9f0', border: '#d8dee9', primary: '#5477a1', primaryInk: '#405d84', primaryForeground: '#ffffff', lineNumber: '#56637e' },
    dark: { background: '#2e3440', foreground: '#d8dee9', card: '#333946', muted: '#3b4252', border: '#3b4252', primary: '#88c0d0', primaryInk: '#98d0e0', primaryForeground: '#080d17', lineNumber: '#a0acc3' },
    vscode: {
      names: { light: 'Nord Light', dark: 'Nord' },
      syntax: {
        light: { comment: '#6d7a96', keyword: '#5e81ac', string: '#5c7a3a', number: '#8f5f8a', function: '#3b6e85', type: '#2d7a78', variable: '#2e3440', control: '#5e81ac' },
        dark: { comment: '#616e88', keyword: '#81a1c1', string: '#a3be8c', number: '#b48ead', function: '#88c0d0', type: '#8fbcbb', variable: '#d8dee9', control: '#81a1c1' },
      },
    },
  },
  {
    id: 'contrast',
    label: 'High Contrast',
    description: "VS Code's High Contrast Dark and Light: black or white, bright borders, the most contrast.",
    light: { background: '#ffffff', foreground: '#000000', card: '#ffffff', muted: '#f2f2f2', border: '#0f4a85', primary: '#0f4a85', primaryInk: '#0f4a85', primaryForeground: '#ffffff', lineNumber: '#292929' },
    dark: { background: '#000000', foreground: '#ffffff', card: '#000000', muted: '#0c141f', border: '#6fc3df', primary: '#f38518', primaryInk: '#ffb46b', primaryForeground: '#000000', lineNumber: '#ffffff' },
    vscode: {
      names: { light: 'Default High Contrast Light', dark: 'Default High Contrast' },
      syntax: {
        light: { comment: '#515151', keyword: '#0f4a85', string: '#0f4a85', number: '#096d48', function: '#5e2cbc', type: '#185e73', variable: '#001080', control: '#b5200d' },
        dark: { comment: '#7ca668', keyword: '#569cd6', string: '#ce9178', number: '#b5cea8', function: '#dcdcaa', type: '#4ec9b0', variable: '#9cdcfe', control: '#c586c0' },
      },
    },
  },
]

/** Whether a colour theme is one of VS Code's (sharp), not Arena's own (calm). */
export const isVsCodeTheme = (t: ColorThemeInfo) => !!t.vscode

export const isColorTheme = (v: unknown): v is ColorThemeId => typeof v === 'string' && (colorThemeIds as readonly string[]).includes(v)

export const colorThemeOf = (id: ColorThemeId): ColorThemeInfo => colorThemes.find((t) => t.id === id)!
