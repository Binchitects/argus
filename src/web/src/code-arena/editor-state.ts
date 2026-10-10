import type * as Monaco from 'monaco-editor'
import { createContext, use } from 'react'

// The editor's shared parts: Monaco's loader, the tabs' shape, the context the
// panels reach the editor through, and quick open's ranking.
export type MonacoModule = typeof import('./monaco')

let loading: Promise<MonacoModule> | null = null
/** Monaco and its setup, loaded with the first file opened (it is most of the page's weight). */
export const loadMonaco = () =>
  (loading ??= import('./monaco').catch((e: unknown) => {
    loading = null
    throw e
  }))

/** macOS (and iPadOS): the editor's keys are ⌘ there, and Ctrl stays the terminal's and the editor's own. */
export const onMac = () => typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)

/** The editor's modifier key, as its tooltips and the welcome name it. */
export const modKey = onMac() ? '⌘' : 'Ctrl'

/** Where to put the cursor in a file opened: a line, and a column and the length to select there, or the lines to select to. */
export interface Reveal {
  line: number
  column?: number
  length?: number
  /** The last line of the lines cited: they are selected whole. */
  endLine?: number
}

/** An editor tab: a file, or the agent's changes to one (before and now, side by side). */
export interface Tab {
  /** The path for a file; "diff:" and the path for the agent's changes. */
  id: string
  kind: 'file' | 'diff'
  path: string
  status: 'loading' | 'ready' | 'binary' | 'tooLarge' | 'error'
  message?: string
  /** The open that is loading it: closed and opened again, or renamed, the load finds its own tab or none. */
  load?: symbol
  dirty: boolean
  /** The language's name, for the status bar. */
  language?: string
}

export interface FileModel {
  path: string
  model: Monaco.editor.ITextModel
  /** The file's version on disk when it was read or saved. */
  version: string
  /** The model's alternative version id at that moment: anything else is unsaved. */
  savedAlt: number
  view: Monaco.editor.ICodeEditorViewState | null
}

export interface DiffModels {
  original: Monaco.editor.ITextModel
  modified: Monaco.editor.ITextModel
}

export type Unsaved = 'save' | 'discard' | 'cancel'

export interface EditorApi {
  tabs: Tab[]
  active: Tab | null
  /** The cursor in the file shown, 1-based. */
  cursor: { line: number; column: number } | null
  open: (path: string, at?: Reveal) => Promise<void>
  openDiff: (path: string) => Promise<void>
  activate: (id: string) => void
  /** Asks first when the file has unsaved changes; false when the person kept it open. */
  close: (id: string) => Promise<boolean>
  save: (id?: string) => Promise<boolean>
  /** Files changed on disk (by the agent, a command): open tabs without unsaved changes show them. All tabs without a path. */
  refresh: (path?: string) => Promise<void>
  /** A file or folder renamed in the explorer: its tabs follow. */
  moved: (from: string, to: string) => void
  /** A file or folder deleted in the explorer: its tabs close, but those with unsaved changes (saving one makes the file again). */
  removed: (path: string) => void
  accept: (path?: string) => Promise<void>
  revert: (path: string) => Promise<void>
  quickOpen: boolean
  setQuickOpen: (open: boolean) => void
  bindEditor: (el: HTMLDivElement | null) => void
  bindDiff: (el: HTMLDivElement | null) => void
}

export const EditorContext = createContext<EditorApi | null>(null)

export function useEditor() {
  const ctx = use(EditorContext)
  if (!ctx) throw new Error('useEditor needs <EditorProvider>')
  return ctx
}

/** The letters typed, in order, in a text: more for runs and word starts; null when they are not all there. */
function letters(text: string, typed: string): number | null {
  let total = 0
  let from = 0
  let last = -2
  for (const ch of typed) {
    const i = text.indexOf(ch, from)
    if (i < 0) return null
    total += 1 + (i === last + 1 ? 5 : 0) + (i === 0 || '/._-'.includes(text[i - 1]!) ? 3 : 0)
    last = i
    from = i + 1
  }
  return total
}

/** A file's score for what was typed: in its name (better, and best at its start), else anywhere in its path; null when it does not match. */
function score(path: string, typed: string): number | null {
  const name = path.slice(path.lastIndexOf('/') + 1)
  const inName = letters(name, typed)
  const inPath = letters(path, typed)
  if (inName === null && inPath === null) return null
  const best = Math.max(inName === null ? 0 : inName + 10, inPath ?? 0)
  return best + (name.startsWith(typed) ? 30 : name.includes(typed) ? 20 : 0)
}

/** The files that match what was typed, best first: the letters in order, as an editor's quick open finds them. */
export function rankFiles(files: string[], typed: string, limit = 50): string[] {
  const q = typed.toLowerCase().replace(/\s+/g, '')
  if (!q) return files.slice(0, limit)
  const scored: [number, string][] = []
  for (const f of files) {
    const s = score(f.toLowerCase(), q)
    if (s !== null) scored.push([s, f])
  }
  return scored
    .sort((a, b) => b[0] - a[0] || a[1].length - b[1].length)
    .slice(0, limit)
    .map(([, f]) => f)
}
