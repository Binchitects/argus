import { api } from '@/lib/api'
import { formatValue } from '@/lib/format'

/** Admin → Storage's data, as /api/admin/storage and its parts answer. */

export interface Amount {
  count: number
  bytes: number
}

export interface Disk {
  /** The host's device ("/dev/nvme0n1p2"), or "app:/library" for a folder only the app sees. */
  id: string
  /** Where the host mounts it, or the folder. */
  name: string
  device: string | null
  fsType: string | null
  size: number
  free: number
  used: number
  percent: number
  source: 'host' | 'app'
  /** What of the stack is on it. */
  holds: string[]
  above: boolean
  /** [epoch ms, used bytes] over the last 30 days. */
  trend: (number | null)[][]
  perDay: number | null
  fullInDays: number | null
}

export interface FileGroup {
  origin: Origin
  kind: string
  state: FileState
  count: number
  bytes: number
}

export interface PersonFiles {
  id: string
  userName: string
  displayName: string
  email: string | null
  count: number
  bytes: number
  /** Their own room in MB (0: no limit); null: the company's. */
  ownMegabytes: number | null
  held: boolean
}

export interface LibraryUse {
  path: string
  bytes: number
  parts: number
  kind: string
  use: 'engine' | 'pictures' | 'video' | 'embeddings' | 'laya' | 'unused'
  models: string[]
  note: string | null
}

export interface LibraryReport {
  dir: string
  exists: boolean
  bytes: number
  files: LibraryUse[]
  folders: { name: string; bytes: number }[]
  partials: { path: string; bytes: number; modified: string; download: string | null }[]
}

export interface Backup {
  name: string
  bytes: number
  at: string | null
  result: string | null
  latest: boolean
}

export interface ArgusStorage {
  data_dir: string
  index_bytes: number
  mirrors_bytes: number
  trees_bytes: number
  packs_bytes: number
  other_bytes: number
  library_dir: string | null
  library_bytes: number | null
  disk: { size_bytes: number; free_bytes: number } | null
}

export interface ArgusPack {
  name: string
  version: string
  size_bytes: number
  source?: 'installed' | 'library'
}

export interface StorageReport {
  at: string
  settings: { alertPercent: number; personMegabytes: number | null; mediaDays: number; backupsKept: number }
  disks: { alertPercent: number; problem: string | null; list: Disk[] }
  databases: {
    list: { name: string; bytes: number; what: string | null }[]
    tables: { database: string; name: string; bytes: number; rows: number; what: string | null }[]
    problem: string | null
  }
  files: { total: Amount | null; groups: FileGroup[] | null; people: PersonFiles[] | null; problem: string | null }
  library: { report: LibraryReport | null; problem: string | null }
  argus: { configured: boolean; report?: ArgusStorage | null; problem?: string; packs?: ArgusPack[] | null; library?: (ArgusPack & { file: string; loaded: boolean })[] | null; packsProblem?: string }
  backups: { dir: string; state: 'ok' | 'missing' | 'unreadable'; bytes: number; otherBytes: number; backups: Backup[] }
  logs: { keeps: string | null; week: { name: string; bytes: number }[]; weekStored: number | null; problem: string | null }
  metrics: { bytes: number | null; keeps: string | null; maxSize: string | null; oldest: string | null; problem: string | null }
  unseen: string[]
  rules: { what: string; rule: string }[]
  /** Each thing measured, a point a day: [epoch ms, bytes]. */
  trends: Record<string, number[][]>
}

export type Origin = 'upload' | 'picture' | 'video' | 'speech' | 'tool'
export type FileState = 'chat' | 'assistant' | 'deleted' | 'none'

export interface FileRow {
  id: string
  userId: string
  person: string
  email: string | null
  name: string
  kind: string
  contentType: string
  origin: Origin
  state: FileState
  bytes: number
  createdAt: string
  chatId: string | null
  chatTitle: string | null
  held: boolean
}

export interface FileList {
  rows: FileRow[]
  total: Amount
  capped: boolean
}

export type CleanupKind = 'deleted-chats' | 'unused-files' | 'old-media' | 'disk-orphans' | 'old-backups' | 'unused-models'

export interface CleanupItem {
  id: string
  name: string
  person: string | null
  bytes: number
  at: string | null
  note: string | null
}

export interface CleanupPlan {
  kind: CleanupKind
  count: number
  bytes: number
  held: Amount
  items: CleanupItem[]
  problem: string | null
  warning: string | null
  days: number | null
  keep: number | null
  /** For one the app does not run (old backups, which it sees read only): the command that does, on the host. */
  command: string | null
}

export interface CleanupDone {
  kind: CleanupKind
  count: number
  bytes: number
  held: Amount
  failed: string[]
}

export const origins: Record<Origin, string> = {
  upload: 'Uploaded',
  picture: 'Picture made',
  video: 'Video made',
  speech: 'Speech made',
  tool: 'Made by a tool',
}

export const states: Record<FileState, string> = {
  chat: 'In a chat',
  assistant: "An assistant's",
  deleted: 'In a deleted chat',
  none: 'In no chat',
}

export const kinds = ['text', 'image', 'audio', 'video', 'file'] as const

export const uses: Record<LibraryUse['use'], string> = {
  engine: 'A model of Admin → Models',
  pictures: 'The picture server',
  video: 'The video server',
  embeddings: 'The embedder',
  laya: 'Laya',
  unused: 'Nothing uses it',
}

/** "1.5 GiB". */
export const size = (bytes: number | null | undefined) => (bytes === null || bytes === undefined ? '—' : formatValue(bytes, 'bytes'))

/** "3 files", "1 file". */
export const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`

/** How much a trend grew over its last <days> days: bytes, or null with too few points. */
export function growth(points: number[][] | undefined, days = 30): number | null {
  if (!points || points.length < 2) return null
  const last = points[points.length - 1]!
  const since = last[0]! - days * 86_400_000
  const first = points.find((p) => p[0]! >= since) ?? points[0]!
  return first === last ? null : last[1]! - first[1]!
}

/** The whole page's picture. */
export const storageQuery = {
  queryKey: ['admin', 'storage'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<StorageReport>('/api/admin/storage', { signal }),
}

/** A filter's "any". */
export const ANY = '__any__'

/** The filters as the API takes them. */
export interface FileQuery {
  q: string
  person: string
  kind: string
  origin: string
  state: string
  min: string
  days: string
  sort: string
}

export const noFilter: FileQuery = { q: '', person: ANY, kind: ANY, origin: ANY, state: ANY, min: ANY, days: ANY, sort: 'size' }

/**
 * "?q=…&origin=picture…": the chosen filters only; an age becomes the time before which files were made, to the
 * minute. The page's query key, so the same through a minute: to the millisecond, each render would ask again.
 */
export function fileSearch(f: FileQuery, now = Date.now()): string {
  const q = new URLSearchParams()
  if (f.q.trim()) q.set('q', f.q.trim())
  for (const key of ['person', 'kind', 'origin', 'state', 'min'] as const) if (f[key] !== ANY) q.set(key, f[key])
  if (f.days !== ANY) q.set('before', new Date(Math.floor(now / 60_000) * 60_000 - Number(f.days) * 86_400_000).toISOString())
  if (f.sort !== 'size') q.set('sort', f.sort)
  const s = q.toString()
  return s ? `?${s}` : ''
}

/** A person's room in bytes: their own (0: no limit), else the company's; null: no limit. */
export function roomOf(p: PersonFiles, company: number | null): number | null {
  const mb = p.ownMegabytes ?? company
  return mb ? mb * 1024 * 1024 : null
}

/** "500", "" (the company's) or "0" (no limit) as the API takes it; undefined when it is not a number. */
export function parseRoom(text: string): number | null | undefined {
  const t = text.trim()
  if (t === '') return null
  return /^\d+$/.test(t) && Number(t) <= 100_000_000 ? Number(t) : undefined
}

/** A clean-up as the page shows it. */
export interface CleanupType {
  kind: CleanupKind
  title: string
  about: string
  /** The number it takes: an age in days, or how many backups to keep. */
  param?: 'days' | 'keep'
  noun: [string, string]
}

/** The clean-ups, in the order the page shows them. */
export const cleanupKinds: CleanupType[] = [
  {
    kind: 'unused-files',
    title: 'Files in no chat',
    about: 'Uploaded and never sent, taken out of an assistant, or left behind by one. A file younger than the age chosen may be one being written: it stays.',
    param: 'days',
    noun: ['file', 'files'],
  },
  {
    kind: 'old-media',
    title: 'Old pictures, videos and speech',
    about: 'What the image, video and speech tools made, past an age. The chats keep their words; the files are gone from them.',
    param: 'days',
    noun: ['file', 'files'],
  },
  {
    kind: 'deleted-chats',
    title: 'Files of deleted chats',
    about: 'A deleted chat goes with its files at once; under legal hold it is only hidden, with its files, until the hold ends. This shows what holds keep, and takes the chats whose hold is over (the hourly sweep would too).',
    noun: ['chat', 'chats'],
  },
  {
    kind: 'disk-orphans',
    title: 'Leftovers on disk',
    about: "Files no database row owns: downloads' parts no download will finish, and Python sandbox jobs left behind.",
    noun: ['leftover', 'leftovers'],
  },
  {
    kind: 'old-backups',
    title: 'Old backups',
    about: 'Backups beyond the newest ones; never the latest, nor the newest that ended well. The app sees the backups read only (they hold every secret of the stack): the preview gives the command that removes them on the host.',
    param: 'keep',
    noun: ['backup', 'backups'],
  },
  {
    kind: 'unused-models',
    title: 'Models nothing uses',
    about: 'Model files no model of Admin → Models uses and no server reads. Choose each one.',
    noun: ['model', 'models'],
  },
]
