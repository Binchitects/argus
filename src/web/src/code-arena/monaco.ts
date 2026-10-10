import * as monaco from 'monaco-editor'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'
import CssWorker from 'monaco-editor/language/css/css.worker?worker'
import HtmlWorker from 'monaco-editor/language/html/html.worker?worker'
import JsonWorker from 'monaco-editor/language/json/json.worker?worker'
import TsWorker from 'monaco-editor/language/typescript/ts.worker?worker'

// Monaco, bundled with the page: its workers are files of the page's own (Vite's
// ?worker imports), so nothing is fetched from anywhere else, ever. This module
// loads when the first file opens (it is large), and once.
self.MonacoEnvironment = {
  getWorker(_id: string, label: string) {
    switch (label) {
      case 'json':
        return new JsonWorker()
      case 'css':
      case 'scss':
      case 'less':
        return new CssWorker()
      case 'html':
      case 'handlebars':
      case 'razor':
        return new HtmlWorker()
      case 'typescript':
      case 'javascript':
        return new TsWorker()
      default:
        return new EditorWorker()
    }
  },
}

// One file at a time, without the project's packages: the imports it cannot
// see would be errors everywhere. Syntax errors and completions stay.
// A model's address is its file's (fileUri): .tsx and .jsx are read with JSX, as the project's compiler reads them.
const ts = monaco.typescript
for (const defaults of [ts.typescriptDefaults, ts.javascriptDefaults]) {
  defaults.setDiagnosticsOptions({ noSemanticValidation: true, noSyntaxValidation: false })
  defaults.setCompilerOptions({
    ...defaults.getCompilerOptions(),
    jsx: ts.JsxEmit.ReactJSX,
    allowJs: true,
    allowNonTsExtensions: true,
    target: ts.ScriptTarget.ESNext,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.NodeJs,
  })
}

// Argus Arena's colours (src/styles/index.css, as sRGB: Monaco takes hex).
monaco.editor.defineTheme('arena-light', {
  base: 'vs',
  inherit: true,
  rules: [],
  colors: {
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
  },
})
monaco.editor.defineTheme('arena-dark', {
  base: 'vs-dark',
  inherit: true,
  rules: [],
  colors: {
    'editor.background': '#0c0e13',
    'editor.foreground': '#e9ebee',
    'editorLineNumber.foreground': '#5d646e',
    'editorLineNumber.activeForeground': '#e9ebee',
    'editor.lineHighlightBackground': '#1c1f2480',
    'editor.selectionBackground': '#4c94ec40',
    'editor.inactiveSelectionBackground': '#4c94ec26',
    'editorCursor.foreground': '#4c94ec',
    'editorWidget.background': '#13161c',
    'editorWidget.border': '#282c31',
    'editorGutter.background': '#0c0e13',
    'focusBorder': '#4c94ec99',
    'diffEditor.insertedTextBackground': '#44b78230',
    'diffEditor.removedTextBackground': '#ed535030',
    'diffEditor.insertedLineBackground': '#44b7821a',
    'diffEditor.removedLineBackground': '#ed53501a',
  },
})

// The editor's font is the app's mono font: measured again once it has loaded.
void document.fonts?.ready.then(() => monaco.editor.remeasureFonts())

export { monaco }

/** A file's model's address: its path in the folder, so the language services know what it is (.tsx: with JSX). */
export const fileUri = (path: string) => monaco.Uri.file('/' + path)

let sides = 0
/** A side of the agent's change to a file: an address of its own each time, never a file's. */
export const diffUri = (side: 'before' | 'after', path: string) => monaco.Uri.from({ scheme: `agent-${side}`, path: '/' + path, query: String(++sides) })

/** The path of a file's model (fileUri); null for any other model. */
export const pathOf = (uri: monaco.Uri) => (uri.scheme === 'file' ? uri.path.slice(1) : null)

export const themeOf = (resolved: 'light' | 'dark') => (resolved === 'dark' ? 'arena-dark' : 'arena-light')

/** A file's language by its name: a name Monaco knows (Dockerfile), else its longest known extension (.d.ts before .ts). */
export function languageOf(path: string): { id: string; name: string } {
  const name = path.slice(path.lastIndexOf('/') + 1).toLowerCase()
  let best: { id: string; name: string; length: number } | null = null
  for (const l of monaco.languages.getLanguages()) {
    const label = l.aliases?.[0] ?? l.id
    if (l.filenames?.some((f) => f.toLowerCase() === name)) return { id: l.id, name: label }
    for (const ext of l.extensions ?? []) {
      if (name.endsWith(ext.toLowerCase()) && ext.length > (best?.length ?? 0)) best = { id: l.id, name: label, length: ext.length }
    }
  }
  return best ? { id: best.id, name: best.name } : { id: 'plaintext', name: 'Plain Text' }
}

export const editorFont = {
  fontFamily: "'JetBrains Mono Variable', ui-monospace, 'SFMono-Regular', monospace",
  fontSize: 13,
  lineHeight: 20,
}
