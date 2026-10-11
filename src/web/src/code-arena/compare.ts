import { nameOf } from './ide-api'

/**
 * One side of a compare: a file as the editor holds it (live, editable, saved with Ctrl+S), as it is saved on disk, at a
 * git revision (HEAD, ":" the index, a commit), as it was before the agent's first change, the clipboard's text, or
 * nothing (a file made or deleted).
 */
export type Side =
  | { kind: 'file'; path: string }
  | { kind: 'disk'; path: string }
  | { kind: 'git'; path: string; rev: string }
  | { kind: 'agent'; path: string }
  | { kind: 'clipboard'; text: string }
  | { kind: 'empty'; path?: string }

/** A side's key, for the tab's id. */
export function sideKey(s: Side): string {
  switch (s.kind) {
    case 'file':
    case 'disk':
    case 'agent':
      return `${s.kind}:${s.path}`
    case 'git':
      return `git:${s.rev}:${s.path}`
    case 'clipboard':
      return `clipboard:${s.text.length}:${hash(s.text)}`
    case 'empty':
      return `empty:${s.path ?? ''}`
  }
}

const hash = (text: string) => {
  let h = 0
  for (let i = 0; i < text.length; i++) h = (Math.imul(31, h) + text.charCodeAt(i)) | 0
  return (h >>> 0).toString(36)
}

/** The path a side is of (none for the clipboard's). */
export const sidePath = (s: Side) => ('path' in s ? s.path : undefined)

/** What a side is called in a compare's title and toolbar, as VS Code names them. */
export function sideLabel(s: Side): string {
  switch (s.kind) {
    case 'file':
      return nameOf(s.path)
    case 'disk':
      return `${nameOf(s.path)} (Saved)`
    case 'git':
      return `${nameOf(s.path)} (${s.rev === ':' ? 'Index' : s.rev === 'HEAD' ? 'HEAD' : s.rev.slice(0, 8)})`
    case 'agent':
      return `${nameOf(s.path)} (before the agent)`
    case 'clipboard':
      return 'Clipboard'
    case 'empty':
      return s.path ? `${nameOf(s.path)} (none)` : 'Empty'
  }
}

/** The tab's id of a compare of two sides: the agent's changes and git's keep the ids they had. */
export function compareId(left: Side, right: Side): string {
  return `compare:${sideKey(left)}|${sideKey(right)}`
}

/** A side's path renamed when a file or folder moves. */
export function renamed(s: Side, rename: (p: string) => string): Side {
  return 'path' in s && s.path !== undefined ? ({ ...s, path: rename(s.path) } as Side) : s
}

/** The file chosen with "Select for Compare", waiting for "Compare with Selected". */
export const compareSelection: { path: string | null } = { path: null }
