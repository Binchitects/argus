import { api, ApiError, ideSocketUrl, ideUrl } from './http'
import type { ServerInfo } from './lsp/client'

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

/** A symbol the code index found: what it is, what holds it, and where (its first and last lines). */
export interface SymbolHit {
  name: string
  kind: string
  container: string | null
  path: string
  line: number
  end: number
}

/** Go to Symbol: symbols by name across the folder (best first), or one file's (in order); building while the index is first made. */
export const symbolsQuery = (q: string, path?: string) => ({
  queryKey: ['code', 'symbols', path ?? '', q] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ symbols: SymbolHit[]; building: boolean }>(`/api/symbols?q=${at(q)}${path ? `&path=${at(path)}` : ''}`, { signal }),
})

/** A symbol of the Outline: its name and kind, what holds it (qualified), its lines and its first line. */
export interface OutlineSymbol {
  name: string
  kind: string
  qualified: string
  parent: string | null
  start: number
  end: number
  signature: string
}

/** The Outline of a file: the symbols of the text the editor holds (unsaved edits too). */
export const outlineOf = (path: string, text: string) => api<{ symbols: OutlineSymbol[]; language: string | null }>('/api/outline', { body: { path, text } })

/** A file's unsaved text kept for hot exit: the path, the text, the version of the file it was typed over. */
export interface Backup {
  path: string
  text: string
  version: string | null
}

/** The unsaved text kept of this folder's tabs: put back after a reload. */
export const listBackups = () => api<{ backups: Backup[] }>('/api/backups')
/** Keeps a file's unsaved text (null: forgets it, the file saved or its changes thrown away). */
export const keepBackup = (path: string, text: string | null, version: string | null) => api('/api/backups', { body: { path, text, version } })

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
/** A file of the folder as it is (a picture, a sound, a video), for the page to show or play: its cookie lets it through. */
export const rawFileUrl = (path: string) => ideUrl(`/api/media?path=${at(path)}`)
/** Saves; with the version it was read at, refused (409 "changed") when the file changed on disk since. */
export const writeFile = (path: string, text: string, version: string | null) => api<Saved>('/api/file', { body: { path, text, version } })
export const createEntry = (path: string, kind: 'file' | 'dir') => api<{ path: string; kind: 'file' | 'dir' }>('/api/files/new', { body: { path, kind } })
export const renameEntry = (from: string, to: string) => api<{ from: string; to: string }>('/api/files/rename', { body: { from, to } })
export const deleteEntry = (path: string) => api('/api/files/delete', { body: { path } })
/** A copy of a file, or of a folder and all in it (links copied as links). */
export const copyEntry = (from: string, to: string) => api<{ from: string; to: string }>('/api/files/copy', { body: { from, to } })
/** A file dropped from the computer, written into the folder (onto one there only when told to overwrite it). */
export const uploadFile = (path: string, file: Blob, overwrite = false) =>
  api<{ path: string; kind: 'file'; size: number }>(`/api/files/upload?path=${at(path)}${overwrite ? '&overwrite=1' : ''}`, { body: file })

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
  /** What found it: "check", a task ("task:" and its label), "review", or "security" (Arena's security review). */
  source?: string
  /** What the agent is asked to fix it, when its source says more than its message (a security review's finding). */
  ask?: string | null
}

/** Arena's latest security review of the folder's repository at its branch, as the IDE last read it; or why there is none. */
export interface SecurityRead {
  project: string | null
  ref: string | null
  count: number
  said?: string
  finishedAt?: string | null
  commit?: string | null
  link?: string
}

export const securityProblemsQuery = {
  queryKey: ['code', 'problems', 'security'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SecurityRead>('/api/problems/security', { signal }),
}

/** Reads Arena's security review again; its findings replace the ones listed. */
export const readSecurity = () => api<SecurityRead>('/api/problems/security', { body: {} })

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

/** A review's finding: where, how bad, what and why, the fix in words, and the exact text to replace when the fix fits the file. */
export interface ReviewFinding {
  index: number
  path: string
  line: number
  endLine: number
  severity: 'critical' | 'major' | 'minor' | 'nit'
  title: string
  why: string
  fix: string
  old: string | null
  new: string | null
  state: 'open' | 'fixed' | 'dismissed'
}

/** The last review (the Review view): of the changes not yet committed, or the branch over its base; while it runs, its steps. */
export interface ReviewRun {
  id: string
  base: string | null
  started: string
  ended: string | null
  running: boolean
  verdict: 'looks good' | 'comments' | 'changes needed' | null
  summary: string | null
  error: string | null
  steps: string[]
  findings: ReviewFinding[]
}

export const reviewQuery = {
  queryKey: ['code', 'review'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ review: ReviewRun | null }>('/api/review', { signal }),
  // While it runs: read again, for its steps and its end.
  refetchInterval: (q: { state: { data?: { review: ReviewRun | null } } }) => (q.state.data?.review?.running ? 1000 : false),
}

export const startReview = (base: string | null) => api<{ review: ReviewRun | null }>('/api/review', { body: base ? { base } : {} })
export const stopReview = () => api<{ review: ReviewRun | null }>('/api/review/stop', { body: {} })
export const markFinding = (index: number, state: ReviewFinding['state']) => api<{ review: ReviewRun | null }>('/api/review/finding', { body: { index, state } })

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
  /** What the repository ignores: a file, or a folder a pattern names (all in it ignored). */
  ignored?: string[]
}

export interface GitCommit {
  hash: string
  subject: string
  author: string
  at: string
}

/** What changed on disk since a version (code-arena's DiskWatch); path "" is Source Control's own (a commit, a checkout). */
export interface DiskChanges {
  version: number
  watching: boolean
  /** The page is behind what is kept: everything it shows is read again. */
  all?: boolean
  changes: { path: string; kind: 'created' | 'changed' | 'deleted' }[]
}

/** Waits on the server (up to 25 seconds) for what changes on disk after version `since`; 0 asks for the version to start from. */
export const diskChanges = (since: number, signal: AbortSignal) => api<DiskChanges>(`/api/fs/changes?since=${since}`, { signal })

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
  queryFn: ({ signal }: { signal: AbortSignal }) => api<GitBranch[]>('/api/git/branches', { signal }),
}
/** A branch: the folder's own (local), a remote's, or a tag. */
export interface GitBranch {
  name: string
  current: boolean
  kind: 'local' | 'remote' | 'tag'
}
/** The local branches, then the remotes' and the tags (Checkout to…). */
export const gitAllBranches = () => api<GitBranch[]>('/api/git/branches?all=1')
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
export const gitCommit = (message: string, all: boolean, amend = false) =>
  api<{ hash: string; status: GitStatus }>('/api/git/commit', { body: amend ? { message, all, amend } : { message, all } })
export const gitSwitch = (branch: string, kind: GitBranch['kind'] = 'local') => api<GitStatus>('/api/git/switch', { body: { branch, kind } })
export const gitFetch = () => api<GitStatus>('/api/git/fetch', { body: {} })
export const gitPull = (rebase: boolean) => api<GitStatus>('/api/git/pull', { body: { rebase } })
/** Pushes the branch; one with no upstream is published (to origin, or the only remote). */
export const gitPush = (force = false) => api<GitStatus>('/api/git/push', { body: { force } })
export const gitSync = (rebase: boolean) => api<GitStatus>('/api/git/sync', { body: { rebase } })
export const gitCreateBranch = (name: string, from?: string) => api<GitStatus>('/api/git/branch/create', { body: from ? { name, from } : { name } })
export const gitRenameBranch = (to: string) => api<GitStatus>('/api/git/branch/rename', { body: { to } })
export const gitDeleteBranch = (name: string, force = false) => api<GitStatus>('/api/git/branch/delete', { body: { name, force } })
export const gitUndoCommit = () => api<GitStatus>('/api/git/undo-commit', { body: {} })

/** Code completion at the editor's cursor: what goes there (empty: nothing). */
export const completeCode = (body: { path: string; prefix: string; suffix: string }, signal: AbortSignal) => api<{ text: string }>('/api/complete', { body, signal })

/** A shell in a terminal of its own: in the folder's root, or in a folder of it (Open in Integrated Terminal). */
export const openTerminal = (cols: number, rows: number, cwd?: string) => api<TerminalInfo>('/api/terminals', { body: cwd ? { cols, rows, cwd } : { cols, rows } })

/** A debuggee's program in a terminal of its own (a debug adapter's runInTerminal). */
export const openProgram = (body: { program: string; args: string[]; cwd?: string; env?: Record<string, string | null>; title?: string; cols: number; rows: number }) => api<TerminalInfo>('/api/terminals', { body })
export const closeTerminal = (id: string) => api('/api/terminals/close', { body: { id } })

/**
 * A terminal's socket, on this page's own server: the browser sends this run's
 * key with it (the cookie the printed address set), as it does with every call.
 */
export const terminalSocketUrl = (id: string, where: Location = window.location) => ideSocketUrl(`/api/terminals/socket?id=${at(id)}`, where)

/** The language servers code-arena knows, as found on this computer or named in config.json. */
export const languageServerList = () => api<ServerInfo[]>('/api/lsp')

/** A language server's last stderr lines, whether it runs, and why its last start was refused. */
export interface LanguageServerLog {
  server: string
  running: boolean
  exitCode: number | null
  lines: string[]
  refused?: string | null
}

export const languageServerLogQuery = (id: string) => ({
  queryKey: ['code', 'lsp', 'log', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<LanguageServerLog>(`/api/lsp/log?server=${encodeURIComponent(id)}`, { signal }),
})

/** A language server's socket (it starts as the socket opens, and stops as it closes): the Language Server Protocol's messages, one per text frame. */
export const lspSocketUrl = (id: string, where: Location = window.location) => ideSocketUrl(`/api/lsp/socket?server=${encodeURIComponent(id)}`, where)

/** The folder a path is in ("" for the working directory). */
export const parentOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')
export const nameOf = (path: string) => path.slice(path.lastIndexOf('/') + 1)
/** A path inside a folder ("" the working directory). */
export const join = (folder: string, name: string) => (folder ? `${folder}/${name}` : name)
/** The path itself, or one inside it. */
export const within = (path: string, folder: string) => path === folder || path.startsWith(`${folder}/`)
