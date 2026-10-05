import { api } from '@/lib/api'

/** A file or folder in a folder's listing; paths are relative to the working directory, with "/". */
export interface Entry {
  name: string
  path: string
  kind: 'dir' | 'file'
  size?: number
  /** A link (that stays inside the working directory). */
  link?: boolean
}

export interface Listing {
  path: string
  entries: Entry[]
}

/** A file as the editor opens it: its text, or why it cannot be edited here. */
export interface OpenedFile {
  path: string
  size: number
  /** Sent back with a save, so a file changed on disk meanwhile is not overwritten unseen. */
  version: string
  text: string | null
  binary?: boolean
  tooLarge?: boolean
}

export interface Saved {
  path: string
  version: string
  size: number
}

export interface SearchMatch {
  line: number
  column: number
  length: number
  /** The match's line (from a little before the match when it is long), and where the match starts in it. */
  preview: string
  start: number
}

export interface SearchResults {
  files: { path: string; matches: SearchMatch[] }[]
  count: number
  truncated: boolean
}

export interface SearchOptions {
  q: string
  case?: boolean
  word?: boolean
  regex?: boolean
  include?: string
  exclude?: string
}

/** A file the agent changed in this run, not accepted or reverted yet. */
export interface Change {
  path: string
  /** The agent made it, or it is gone now. */
  created: boolean
  deleted: boolean
  added: number
  removed: number
}

/** The text before the agent (null: it made the file) and now (null: the file is gone). */
export interface ChangeTexts {
  path: string
  original: string | null
  modified: string | null
  version: string
}

export interface TerminalInfo {
  id: string
  title: string
  pid: number
  cols: number
  rows: number
  exitCode: number | null
}

const at = (path: string) => encodeURIComponent(path)

/** A folder's entries ("" the working directory): folders first, then files. */
export const folderQuery = (path: string) => ({
  queryKey: ['code', 'files', 'dir', path] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Listing>(path ? `/api/files?path=${at(path)}` : '/api/files', { signal }),
})

/** Every file of the working directory (as git sees them), for quick open. */
export const allFilesQuery = {
  queryKey: ['code', 'files', 'all'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ files: string[]; truncated: boolean }>('/api/files/all', { signal }),
}

export const changesQuery = {
  queryKey: ['code', 'changes'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Change[]>('/api/changes', { signal }),
}

export const terminalsQuery = {
  queryKey: ['code', 'terminals'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<TerminalInfo[]>('/api/terminals', { signal }),
}

export const searchQuery = (o: SearchOptions) => {
  const params = new URLSearchParams({ q: o.q })
  if (o.case) params.set('case', '1')
  if (o.word) params.set('word', '1')
  if (o.regex) params.set('regex', '1')
  if (o.include?.trim()) params.set('include', o.include.trim())
  if (o.exclude?.trim()) params.set('exclude', o.exclude.trim())
  return {
    queryKey: ['code', 'search', params.toString()] as const,
    queryFn: ({ signal }: { signal: AbortSignal }) => api<SearchResults>(`/api/search?${params}`, { signal }),
  }
}

export const readFile = (path: string) => api<OpenedFile>(`/api/file?path=${at(path)}`)
/** Saves; with the version it was read at, refused (409 "changed") when the file changed on disk since. */
export const writeFile = (path: string, text: string, version: string | null) => api<Saved>('/api/file', { body: { path, text, version } })
export const createEntry = (path: string, kind: 'file' | 'dir') => api<{ path: string; kind: 'file' | 'dir' }>('/api/files/new', { body: { path, kind } })
export const renameEntry = (from: string, to: string) => api<{ from: string; to: string }>('/api/files/rename', { body: { from, to } })
export const deleteEntry = (path: string) => api('/api/files/delete', { body: { path } })

export const changeTexts = (path: string) => api<ChangeTexts>(`/api/changes/diff?path=${at(path)}`)
/** Keeps the agent's change to a file (all of them without one). */
export const acceptChange = (path?: string) => api<Change[]>('/api/changes/accept', { body: path ? { path } : {} })
/** Puts the file back as it was before the agent (deletes a file it made). */
export const revertChange = (path: string) => api<Change[]>('/api/changes/revert', { body: { path } })

/**
 * The page's own preferences, kept by code-arena in its data folder: the browser
 * keeps a page's storage per port, and each run takes a new one. What is there
 * is checked when read (another version may have written it).
 */
export interface Preferences {
  layout?: Record<string, unknown>
  theme?: unknown
}

/** Read once, when the page opens. */
export const preferencesQuery = {
  queryKey: ['code', 'preferences'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Preferences>('/api/preferences', { signal }),
  staleTime: Infinity,
}

/** Keeps these keys (the others stay as they were); keepalive when the page is closing. */
export const savePreferences = (change: Preferences, keepalive = false) => api<Preferences>('/api/preferences', { body: change, keepalive })

export const openTerminal = (cols: number, rows: number) => api<TerminalInfo>('/api/terminals', { body: { cols, rows } })
export const closeTerminal = (id: string) => api('/api/terminals/close', { body: { id } })

/**
 * A terminal's socket, on this page's own server: the browser sends this run's
 * key with it (the cookie the printed address set), as it does with every call.
 */
export const terminalSocketUrl = (id: string, where: Location = window.location) =>
  `${where.protocol === 'https:' ? 'wss' : 'ws'}://${where.host}/api/terminals/socket?id=${at(id)}`

/** The folder a path is in ("" for the working directory). */
export const parentOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')
export const nameOf = (path: string) => path.slice(path.lastIndexOf('/') + 1)
/** A path inside a folder ("" the working directory). */
export const join = (folder: string, name: string) => (folder ? `${folder}/${name}` : name)
/** The path itself, or one inside it. */
export const within = (path: string, folder: string) => path === folder || path.startsWith(`${folder}/`)
