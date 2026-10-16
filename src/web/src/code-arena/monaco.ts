import * as monaco from 'monaco-editor'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'
import CssWorker from 'monaco-editor/language/css/css.worker?worker'
import HtmlWorker from 'monaco-editor/language/html/html.worker?worker'
import JsonWorker from 'monaco-editor/language/json/json.worker?worker'
import TsWorker from 'monaco-editor/language/typescript/ts.worker?worker'
import { keepEditorFormatters } from './format'
import { globMatch } from './glob'
import { monacoLanguageId } from './settings-schema'

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

// The editor's own formatters (TypeScript's, JSON's, CSS's, HTML's) are kept as their services register, before any
// file opens: they still format where the project's formatter takes their place (see format.ts).
keepEditorFormatters(monaco.languages)

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

// VS Code's JSON files: comments and trailing commas allowed, and settings.json and keybindings.json checked against what
// Code Arena reads (schemas() sets them again as commands come and go).
monaco.json.jsonDefaults.setDiagnosticsOptions({ validate: true, allowComments: true, trailingCommas: 'ignore', enableSchemaRequest: false, schemas: [] })

/** The schemas the settings files are checked with: settings.json's, keybindings.json's (by command id), and more given. */
export function schemas(list: { uri: string; fileMatch: string[]; schema: unknown }[]) {
  monaco.json.jsonDefaults.setDiagnosticsOptions({ validate: true, allowComments: true, trailingCommas: 'ignore', enableSchemaRequest: false, schemas: list })
}

// The editor's font is the app's mono font: measured again once it has loaded.
void document.fonts?.ready.then(() => monaco.editor.remeasureFonts())

export { monaco }

/** A file's model's address: its path in the folder, so the language services know what it is (.tsx: with JSX); the person's own settings files (user:settings.json) where VS Code keeps them. */
export const fileUri = (path: string) => (path.startsWith('user:') ? monaco.Uri.from({ scheme: 'vscode-userdata', path: `/User/${path.slice(5)}` }) : monaco.Uri.file('/' + path))

let sides = 0
/** A side of the agent's change to a file: an address of its own each time, never a file's. */
export const diffUri = (side: 'before' | 'after', path: string) => monaco.Uri.from({ scheme: `agent-${side}`, path: '/' + path, query: String(++sides) })

/** The path of a file's model (fileUri); null for any other model. */
export const pathOf = (uri: monaco.Uri) => (uri.scheme === 'file' ? uri.path.slice(1) : null)


/** A file's language by its name: a name Monaco knows (Dockerfile), else its longest known extension (.d.ts before .ts). */
export function languageOf(path: string, associations: Record<string, string> = {}): { id: string; name: string } {
  const name = path.slice(path.lastIndexOf('/') + 1).toLowerCase()
  const languages = monaco.languages.getLanguages()
  let best: { id: string; name: string; length: number } | null = null
  // "files.associations": a name or a pattern (*.inc, **/build/*.txt) to a language id.
  for (const [pattern, id] of Object.entries(associations)) {
    // VS Code's ids (typescriptreact, shellscript…) are Monaco's language for them.
    const l = languages.find((x) => x.id === monacoLanguageId(id))
    if (l && globMatch(pattern, pattern.includes('/') ? path : name)) return { id: l.id, name: l.aliases?.[0] ?? l.id }
  }
  for (const l of languages) {
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
