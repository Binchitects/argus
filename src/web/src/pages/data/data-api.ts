import { api } from '@/lib/api'

export interface DataColumn {
  name: string
  type: string
  comment: string | null
}

export interface DataTable {
  name: string
  kind: string
  comment: string | null
  columns: DataColumn[]
}

export interface DataSourceBrief {
  id: string
  name: string
  description: string
  tables: DataTable[]
}

/** A column of a result: number, date, time, bool or text. */
export interface ResultColumn {
  name: string
  type: 'number' | 'date' | 'time' | 'bool' | 'text'
}

export type Cell = string | number | boolean | null

export interface DataResult {
  columns: ResultColumn[]
  rows: Cell[][]
  truncated: boolean
  milliseconds: number
}

export type ChartType = 'bar' | 'line' | 'number' | 'table'

export interface ChartSpec {
  type?: ChartType | string
  x?: string
  y?: string[]
}

export interface DataAnswer {
  question: string
  sql: string
  explanation: string
  title: string
  chart: ChartSpec
  result: DataResult | null
  error: string | null
  model: string | null
}

export type BoardReach = 'private' | 'groups' | 'company'

export interface Board {
  id: string
  name: string
  description: string
  reach: BoardReach
  groupIds: string[]
  createdAt: string
  updatedAt: string
  owner: string | null
  mine: boolean
  pins: number
}

export type Refresh = 'off' | 'hourly' | 'daily'

export interface Pin {
  id: string
  boardId: string
  sourceId: string
  source: string | null
  title: string
  question: string
  sql: string
  chart: ChartSpec
  refresh: Refresh
  position: number
  result: DataResult | null
  ranAt: string | null
  error: string | null
}

export const sourcesQuery = {
  queryKey: ['data', 'sources'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DataSourceBrief[]>('/api/data/sources', { signal }),
}

export const boardsQuery = {
  queryKey: ['data', 'boards'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Board[]>('/api/data/boards', { signal }),
}

export const boardQuery = (id: string) => ({
  queryKey: ['data', 'boards', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ board: Board; pins: Pin[] }>(`/api/data/boards/${id}`, { signal }),
})

export const askData = (body: { sourceId: string; question: string; earlier?: { question: string; sql: string } }) => api<DataAnswer>('/api/data/ask', { body })

export const runSql = (body: { sourceId: string; sql: string; question?: string }) => api<DataResult>('/api/data/run', { body })

export const addBoard = (body: { name: string; description?: string; reach?: BoardReach; groupIds?: string[] }) => api<Board>('/api/data/boards', { body })

export const changeBoard = (id: string, body: Partial<{ name: string; description: string; reach: BoardReach; groupIds: string[] }>) =>
  api<Board>(`/api/data/boards/${id}`, { method: 'PUT', body })

export const removeBoard = (id: string) => api(`/api/data/boards/${id}`, { method: 'DELETE' })

export const pinChart = (boardId: string, body: { sourceId: string; title: string; question: string; sql: string; chart: ChartSpec; refresh: Refresh }) =>
  api<Pin>(`/api/data/boards/${boardId}/pins`, { body })

export const changePin = (id: string, body: Partial<{ title: string; chart: ChartSpec; refresh: Refresh; position: number }>) =>
  api<Pin>(`/api/data/pins/${id}`, { method: 'PUT', body })

export const unpin = (id: string) => api(`/api/data/pins/${id}`, { method: 'DELETE' })

export const refreshPin = (id: string) => api<Pin>(`/api/data/pins/${id}/refresh`, { body: {} })

export const reachLabels: Record<BoardReach, string> = { private: 'Only me', groups: 'Some groups', company: 'Everyone' }

export const refreshLabels: Record<Refresh, string> = { off: 'By hand', hourly: 'Every hour', daily: 'Every day' }
