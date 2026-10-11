import { api, ApiError } from '@/lib/api'

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

/** Replaces what a search finds (in these files only, when given): how many in each file. */
export const replaceAll = (o: SearchOptions, replacement: string, paths?: string[]) =>
  api<{ files: { path: string; replaced: number; version: string }[]; count: number; truncated: boolean }>('/api/replace', { body: { ...o, replacement, ...(paths && { paths }) } })

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
export const acceptChange = (path?: string, before?: string) => api<Change[]>('/api/changes/accept', { body: path ? (before === undefined ? { path } : { path, before }) : {} })
/** Puts the file back as it was before the agent (deletes a file it made). */
export const revertChange = (path: string) => api<Change[]>('/api/changes/revert', { body: { path } })

/**
 * The page's own preferences, kept by code-arena in its data folder: the browser
 * keeps a page's storage per port, and each run takes a new one. What is there
 * is checked when read (another version may have written it).
 */
export interface Preferences {
  layout?: Record<string, unknown>
  /** The palette's commands run last, newest first. */
  paletteRecent?: unknown
}

/** Read once, when the page opens. */
export const preferencesQuery = {
  queryKey: ['code', 'preferences'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Preferences>('/api/preferences', { signal }),
  staleTime: Infinity,
}

/** Keeps these keys (the others stay as they were); keepalive when the page is closing. */
export const savePreferences = (change: Preferences, keepalive = false) => api<Preferences>('/api/preferences', { body: change, keepalive })

/** The person's own settings.json or keybindings.json, in code-arena's config folder (version "none": not there yet). */
export interface UserFile {
  name: UserFileName
  text: string
  version: string
}

export type UserFileName = 'settings.json' | 'keybindings.json'

export const userFileQuery = (name: UserFileName) => ({
  queryKey: ['code', 'user-file', name] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<UserFile>(`/api/user-files?name=${name}`, { signal }),
  staleTime: 5_000,
  refetchOnWindowFocus: true,
})

export const writeUserFile = (name: UserFileName, text: string, version: string | null) => api<{ name: string; version: string }>('/api/user-files', { body: { name, text, version } })

/** VS Code's own copy of the file on this computer, to bring over. */
export const vscodeUserFile = (name: UserFileName) => api<{ name: string; text: string; from: string }>(`/api/user-files/vscode?name=${name}`)

/** A file of the project read for its settings (.vscode/settings.json…): null when it is not there. */
export const projectFileQuery = (path: string) => ({
  queryKey: ['code', 'project-file', path] as const,
  queryFn: async ({ signal }: { signal: AbortSignal }) => {
    try {
      return await api<OpenedFile>(`/api/file?path=${at(path)}`, { signal })
    } catch (e) {
      if (e instanceof ApiError && e.http === 404) return null
      throw e
    }
  },
  staleTime: 5_000,
  refetchOnWindowFocus: true,
})

/** What this computer keeps for this project: its trust, build configuration, and the page's own keys. */
export interface WorkspaceState {
  trust?: 'trusted' | 'restricted'
  buildConfiguration?: 'Debug' | 'Release'
  [key: string]: unknown
}

export const workspaceStateQuery = {
  queryKey: ['code', 'workspace-state'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<WorkspaceState>('/api/workspace/state', { signal }),
  staleTime: Infinity,
}

export const saveWorkspaceState = (change: Record<string, unknown>, keepalive = false) => api<WorkspaceState>('/api/workspace/state', { body: change, keepalive })

export const trustWorkspace = (trusted: boolean | null) => api<WorkspaceState>('/api/workspace/trust', { body: trusted === null ? {} : { trusted } })

/** A formatter for a file, as the server found it: whether it can run here, and why not. */
export interface Formatter {
  id: string
  name: string
  version?: string
  source: 'project' | 'global' | 'config'
  available: boolean
  /** Why it cannot run: "trust" (it lives in the folder, which is not trusted), "missing", "node". */
  reason?: string
  /** What the person can do about it. */
  hint?: string
  /** It formats a part of a file. */
  range: boolean
  path?: string
}

export interface Formatters {
  language: string | null
  formatters: Formatter[]
  /** The first that can run; null: none. */
  default: string | null
}

export const formattersQuery = (path: string, language?: string) => ({
  queryKey: ['code', 'formatters', path, language ?? ''] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Formatters>(`/api/formatters?path=${at(path)}${language ? `&language=${encodeURIComponent(language)}` : ''}`, { signal }),
  staleTime: 10_000,
})

export const formatText = (
  body: { path: string; text: string; formatter?: string; range?: { start: number; end: number }; options?: { tabSize: number; insertSpaces: boolean; prettier?: Record<string, unknown> }; language?: string },
  signal?: AbortSignal,
) => api<{ text: string; formatter: string; source: string }>('/api/format', { body, signal })

/** A problem the project's check found: a file of the folder, its line and column (from 1), how bad, its code and what it says. */
export interface CheckProblem {
  path: string
  line: number
  column: number
  severity: 'error' | 'warning' | 'info'
  code: string | null
  message: string
}

/** The check's last run: its command (or the one it would run), whether one runs now, when, its exit code, and what it found. */
export interface CheckRun {
  command: string | null
  running: boolean
  ran: string | null
  exitCode: number | null
  /** What it printed, when it failed and nothing in it could be read as a problem. */
  said: string | null
  problems: CheckProblem[]
}

export const problemsQuery = {
  queryKey: ['code', 'problems'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CheckRun>('/api/problems', { signal }),
  // A check running (started from another page, or before this one opened): read again until it is done.
  refetchInterval: (q: { state: { data?: CheckRun } }) => (q.state.data?.running ? 2000 : false),
}

/** Runs the project's check (waits for the one running, when one is); what it found. */
export const runCheck = () => api<CheckRun>('/api/problems/run', { body: {} })

/** A file changed since the last commit: in the index (staged) and in the folder (not), as git's letters (M, A, D, R; ? untracked). */
export interface GitFile {
  path: string
  staged: string | null
  changed: string | null
  /** A rename's old path. */
  from: string | null
}

export interface GitStatus {
  /** False: the folder is in no git repository. */
  repository: boolean
  branch?: string | null
  ahead?: number | null
  behind?: number | null
  files?: GitFile[]
}

export interface GitCommit {
  hash: string
  subject: string
  author: string
  at: string
}

export const gitStatusQuery = {
  queryKey: ['code', 'git', 'status'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<GitStatus>('/api/git/status', { signal }),
}
export const gitLogQuery = {
  queryKey: ['code', 'git', 'log'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<GitCommit[]>('/api/git/log', { signal }),
}
export const gitBranchesQuery = {
  queryKey: ['code', 'git', 'branches'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ name: string; current: boolean }[]>('/api/git/branches', { signal }),
}
/** A file as a revision has it: HEAD, ":" (the index) or a commit. */
export interface GitShown {
  path: string
  rev: string
  commit: string | null
  exists: boolean
  size: number
  text: string | null
  binary: boolean
  tooLarge: boolean
}
export const gitShow = (path: string, rev: string) => api<GitShown>(`/api/git/show?path=${at(path)}&rev=${encodeURIComponent(rev)}`)

/** A commit that changed a file, with the file's name in it. */
export interface GitFileCommit extends GitCommit {
  status: string | null
  pathAt: string | null
  from: string | null
}
export const gitFileLog = (path: string, limit = 50) => api<GitFileCommit[]>(`/api/git/log?path=${at(path)}&limit=${limit}`)

/** A commit and the folder's files it changed. */
export interface GitCommitFiles {
  hash: string
  parents: string[]
  subject: string
  author: string
  at: string
  files: { path: string; status: string; from: string | null }[]
  truncated: boolean
}
export const gitCommitFiles = (hash: string) => api<GitCommitFiles>(`/api/git/commit?hash=${encodeURIComponent(hash)}`)

/** Stages exactly this text as the file's (the file in the folder left alone). */
export const gitStageText = (path: string, text: string) => api<GitStatus>('/api/git/stage-text', { body: { path, text } })
export const gitStage = (paths: string[]) => api<GitStatus>('/api/git/stage', { body: { paths } })
export const gitUnstage = (paths: string[]) => api<GitStatus>('/api/git/unstage', { body: { paths } })
export const gitDiscard = (paths: string[]) => api<GitStatus>('/api/git/discard', { body: { paths } })
export const gitCommit = (message: string, all: boolean) => api<{ hash: string; status: GitStatus }>('/api/git/commit', { body: { message, all } })
export const gitSwitch = (branch: string) => api<GitStatus>('/api/git/switch', { body: { branch } })

/** Code completion at the editor's cursor: what goes there (empty: nothing). */
export const completeCode = (body: { path: string; prefix: string; suffix: string }, signal: AbortSignal) => api<{ text: string }>('/api/complete', { body, signal })

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
