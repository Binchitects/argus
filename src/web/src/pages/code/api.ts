import { api } from '@/lib/api'

export interface ReviewBrief {
  id: string
  project: string
  iid: number | null
  fromRef: string | null
  toRef: string | null
  title: string
  webUrl: string | null
  status: 'queued' | 'running' | 'done' | 'failed'
  step: string | null
  verdict: 'looks_good' | 'comments' | 'changes' | null
  risk: 'low' | 'medium' | 'high' | null
  files: number
  createdAt: string
  finishedAt: string | null
  error: string | null
  /** The project's automatic reviews that started it; null: a person did. */
  ruleId?: string | null
  /** webhook, check or schedule: what started an automatic one. */
  trigger?: 'webhook' | 'check' | 'schedule' | null
}

export interface Finding {
  id: string
  path: string
  line: number | null
  severity: 'critical' | 'major' | 'minor' | 'nit'
  category: string | null
  title: string
  body: string
  suggestion: string | null
  /** The second pass could not refute it (true), or did (false, with why). */
  verified: boolean | null
  verifyNote: string | null
  state: 'open' | 'resolved' | 'dismissed'
}

export interface Review {
  review: ReviewBrief
  summary: string | null
  ci: string | null
  model: string | null
  headSha: string | null
  previousId: string | null
  roomId: string | null
  promptTokens: number
  completionTokens: number
  by: string
  mine: boolean
  findings: Finding[]
}

export interface MergeRequestRow {
  iid: number
  title: string
  state: string
  draft?: boolean
  author?: string
  source_branch?: string
  target_branch?: string
  updated_at?: string
  web_url?: string
}

export const reviewsQuery = {
  queryKey: ['code', 'reviews'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ review: ReviewBrief; open: number; mine: boolean }[]>('/api/code/reviews', { signal }),
  refetchInterval: (q: { state: { data?: { review: ReviewBrief }[] } }) => (q.state.data?.some((r) => r.review.status === 'running' || r.review.status === 'queued') ? 3000 : false),
}

export const reviewQuery = (id: string) => ({
  queryKey: ['code', 'review', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Review>(`/api/code/reviews/${id}`, { signal }),
  refetchInterval: (q: { state: { data?: Review } }) => (q.state.data && (q.state.data.review.status === 'running' || q.state.data.review.status === 'queued') ? 2000 : false),
})

export const mergeRequestsQuery = (project: string, state: string) => ({
  queryKey: ['code', 'mrs', project, state] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MergeRequestRow[]>(`/api/code/mrs?project=${encodeURIComponent(project)}&state=${state}`, { signal }),
  enabled: project.trim().length > 0,
})

export const startReview = (body: { project: string; iid?: number; from?: string; to?: string }) => api<{ id: string }>('/api/code/reviews', { body })

export const reviewAgain = (id: string) => api<{ id: string }>(`/api/code/reviews/${id}/again`, { body: {} })

export const decideFinding = (id: string, findingId: string, state: Finding['state']) => api(`/api/code/reviews/${id}/findings/${findingId}`, { method: 'PUT', body: { state } })

export const reviewRoom = (id: string) => api<{ id: string }>(`/api/code/reviews/${id}/room`, { body: {} })

export const askCode = (body: { question: string; project?: string; ref?: string; iid?: number }) => api<{ id: string; message: string }>('/api/code/ask', { body })

export const verdictLabel: Record<NonNullable<ReviewBrief['verdict']>, string> = { looks_good: 'Looks good', comments: 'Comments', changes: 'Changes suggested' }

/** A project's automatic reviews (Code → Automatic reviews). */
export interface ReviewRule {
  id: string
  project: string
  enabled: boolean
  mergeRequests: boolean
  targetBranches: string[]
  skipDrafts: boolean
  checkMinutes: number
  branch: string | null
  base: string | null
  cron: string | null
  timeZone: string
  roomId: string | null
  mostPerDay: number
  checkedAt: string | null
  nextRunAt: string | null
  lastError: string | null
  createdAt: string
  by: string | null
  today: number
  hookUrl: string
}

export type RuleChange = Partial<Pick<ReviewRule, 'project' | 'enabled' | 'mergeRequests' | 'targetBranches' | 'skipDrafts' | 'checkMinutes' | 'branch' | 'base' | 'cron' | 'timeZone' | 'roomId' | 'mostPerDay'>>

export const rulesQuery = {
  queryKey: ['code', 'rules'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ReviewRule[]>('/api/code/rules', { signal }),
}

export const addRule = (body: RuleChange) => api<{ rule: ReviewRule; secret: string }>('/api/code/rules', { body })

export const changeRule = (id: string, body: RuleChange) => api<ReviewRule>(`/api/code/rules/${id}`, { method: 'PUT', body })

export const removeRule = (id: string) => api(`/api/code/rules/${id}`, { method: 'DELETE' })

export const newRuleSecret = (id: string) => api<{ secret: string }>(`/api/code/rules/${id}/secret`, { body: {} })

export const checkRule = (id: string) => api<{ said: string[] }>(`/api/code/rules/${id}/check`, { body: {} })

export interface Workspace {
  id: string
  kind: 'server' | 'machine'
  name: string
  project: string | null
  branch: string | null
  state: 'running' | 'asleep' | 'failed'
  error: string | null
  createdAt: string
  usedAt: string
}

export const workspacesQuery = {
  queryKey: ['studio', 'workspaces'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ server: boolean; workspaces: Workspace[] }>('/api/studio/workspaces', { signal }),
}

export const createWorkspace = (body: { project: string; branch?: string }) => api<Workspace>('/api/studio/workspaces', { body })
export const startWorkspace = (id: string) => api<Workspace>(`/api/studio/workspaces/${id}/start`, { body: {} })
export const stopWorkspace = (id: string) => api<Workspace>(`/api/studio/workspaces/${id}/stop`, { body: {} })
export const deleteWorkspace = (id: string) => api(`/api/studio/workspaces/${id}`, { method: 'DELETE' })
