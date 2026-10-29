import * as monaco from 'monaco-editor'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'
import CssWorker from 'monaco-editor/language/css/css.worker?worker'
import HtmlWorker from 'monaco-editor/language/html/html.worker?worker'
import JsonWorker from 'monaco-editor/language/json/json.worker?worker'
import TsWorker from 'monaco-editor/language/typescript/ts.worker?worker'
import { keepEditorFormatters } from './format'
import { globMatch } from './glob'
import { languageByFirstLine } from './language-detect'
import { monacoLanguageId } from './settings-schema'
import { isOutside } from './whole-path'

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

// The editor's own language services (Monaco's TypeScript, JSON, CSS and HTML workers) step aside for the files a
// language server keeps (stepAside), so nothing is answered twice; for the other models of the language (a compare's
// side, the person's settings.json) they stay. Monaco registers their features once, as the first file of the language
// opens, and has no way to take them back (TypeScript's and CSS's mode configuration is read then only): each is
// wrapped as it is registered, and answers nothing for a model its language's server keeps; its markers there (owned
// by the language's id) are dropped meanwhile. Code Arena's own providers are object literals, never wrapped; Monaco's
// are instances of its adapters.
type Kept = (model: monaco.editor.ITextModel) => boolean
const builtIn = new Set(['typescript', 'javascript', 'json', 'css', 'scss', 'less', 'html', 'handlebars', 'razor'])
const aside = new Map<string, Kept>()
const isModel = (x: unknown): x is monaco.editor.ITextModel => !!x && typeof x === 'object' && 'uri' in x && typeof (x as { getLanguageId?: unknown }).getLanguageId === 'function'
const api = monaco.languages as unknown as Record<string, unknown>
for (const name of Object.keys(api).filter((k) => /^register\w+Provider$/.test(k))) {
  const register = api[name] as (selector: unknown, provider: object, ...rest: unknown[]) => monaco.IDisposable
  api[name] = (selector: unknown, provider: object, ...rest: unknown[]) => {
    if (typeof selector !== 'string' || !builtIn.has(selector) || !provider || Object.getPrototypeOf(provider) === Object.prototype) return register(selector, provider, ...rest)
    const gated = new Proxy(provider, {
      get(target, key, receiver) {
        const v: unknown = Reflect.get(target, key, receiver)
        if (typeof v !== 'function' || typeof key !== 'string' || !key.startsWith('provide')) return v
        // Every provide… takes the model first.
        return (...args: unknown[]) => (isModel(args[0]) && aside.get(selector)?.(args[0]) ? undefined : (v as (...a: unknown[]) => unknown).apply(target, args))
      },
    })
    return register(selector, gated, ...rest)
  }
}
const setMarkers = monaco.editor.setModelMarkers
;(monaco.editor as unknown as Record<string, unknown>).setModelMarkers = (model: monaco.editor.ITextModel, owner: string, markers: monaco.editor.IMarkerData[]) =>
  setMarkers(model, owner, aside.get(owner)?.(model) ? [] : markers)

/** The editor's own service for a language steps aside for the models a language server keeps (null: it comes back). */
export function stepAside(language: string, kept: Kept | null) {
  if (!builtIn.has(language) || (aside.has(language) && kept)) return
  if (kept) {
    aside.set(language, kept)
    for (const model of monaco.editor.getModels()) if (model.getLanguageId() === language && kept(model)) setMarkers(model, language, [])
    return
  }
  if (!aside.delete(language)) return
  // Its diagnostics worked out again (each worker does so as its options change).
  if (language === 'typescript' || language === 'javascript') {
    const d = language === 'typescript' ? ts.typescriptDefaults : ts.javascriptDefaults
    d.setDiagnosticsOptions(d.getDiagnosticsOptions())
  } else if (language === 'json') monaco.json.jsonDefaults.setDiagnosticsOptions(monaco.json.jsonDefaults.diagnosticsOptions)
  else if (language === 'css' || language === 'scss' || language === 'less') {
    const d = language === 'css' ? monaco.css.cssDefaults : language === 'scss' ? monaco.css.scssDefaults : monaco.css.lessDefaults
    d.setOptions(d.options)
  }
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

/** A path outside the folder open (in a folder added to the workspace, or named with @): whole, from / or a drive. */
export { isOutside }

/**
 * A file's model's address: its path in the folder, so the language services know what it is (.tsx: with JSX); the
 * person's own settings files (user:settings.json) where VS Code keeps them; a file outside the folder under a scheme of
 * its own (the folder's language servers are not given it).
 */
export const fileUri = (path: string) =>
  path.startsWith('user:')
    ? monaco.Uri.from({ scheme: 'vscode-userdata', path: `/User/${path.slice(5)}` })
    : isOutside(path)
      ? monaco.Uri.from({ scheme: 'outside', path: '/' + encodeURIComponent(path) })
      : monaco.Uri.file('/' + path)

let sides = 0
/** A side of the agent's change to a file: an address of its own each time, never a file's. */
export const diffUri = (side: 'before' | 'after', path: string) => monaco.Uri.from({ scheme: `agent-${side}`, path: '/' + path.replace(/^\/+/, ''), query: String(++sides) })

/** The path of a file's model (fileUri); null for any other model. */
export const pathOf = (uri: monaco.Uri) => (uri.scheme === 'file' ? uri.path.slice(1) : uri.scheme === 'outside' ? decodeURIComponent(uri.path.slice(1)) : null)


/**
 * A file's language by its name: a name Monaco knows (Dockerfile), else its longest known extension (.d.ts before .ts);
 * a name that says none, by its first line when given (#!/bin/bash).
 */
export function languageOf(path: string, associations: Record<string, string> = {}, firstLine?: string): { id: string; name: string } {
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
  if (best) return { id: best.id, name: best.name }
  const byLine = firstLine ? languageByFirstLine(firstLine, languages) : null
  const l = byLine ? languages.find((x) => x.id === byLine) : undefined
  return l ? { id: l.id, name: l.aliases?.[0] ?? l.id } : { id: 'plaintext', name: 'Plain Text' }
}


export const editorFont = {
  fontFamily: "'JetBrains Mono Variable', ui-monospace, 'SFMono-Regular', monospace",
  fontSize: 13,
  lineHeight: 20,
}
