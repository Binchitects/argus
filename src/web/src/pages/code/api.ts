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
  /** Shared with others (everyone, groups or people). */
  shared?: boolean
}

export type ReviewReach = 'Private' | 'Groups' | 'Company'

/** Whom its owner shared it with. */
export interface ReviewSharing {
  reach: ReviewReach
  groups: { id: string; name: string }[]
  people: { id: string; userName: string; displayName: string | null }[]
  sharedAt: string | null
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
  /** Its owner's only. */
  share: ReviewSharing | null
  /** Shared with the person: they read it, and change nothing of it. */
  readOnly: boolean
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
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ review: ReviewBrief; open: number; mine: boolean; sharedBy: string | null }[]>('/api/code/reviews', { signal }),
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

export const shareReview = (id: string, body: { reach: ReviewReach; groups: string[]; people: string[] }) => api(`/api/code/reviews/${id}/share`, { method: 'PUT', body })

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
  /** A server's: running, asleep or failed; a machine's: connected or offline. */
  state: 'running' | 'asleep' | 'failed' | 'connected' | 'offline'
  error: string | null
  createdAt: string
  usedAt: string
}

export const workspacesQuery = {
  queryKey: ['studio', 'workspaces'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ server: boolean; workspaces: Workspace[] }>('/api/studio/workspaces', { signal }),
  // A machine connects and disconnects on its own: read again now and then while there is one.
  refetchInterval: (q: { state: { data?: { workspaces: Workspace[] } } }) => (q.state.data?.workspaces.some((w) => w.kind === 'machine') ? 10_000 : false),
}

export const createWorkspace = (body: { project: string; branch?: string }) => api<Workspace>('/api/studio/workspaces', { body })
export const startWorkspace = (id: string) => api<Workspace>(`/api/studio/workspaces/${id}/start`, { body: {} })
export const stopWorkspace = (id: string) => api<Workspace>(`/api/studio/workspaces/${id}/stop`, { body: {} })
export const deleteWorkspace = (id: string) => api(`/api/studio/workspaces/${id}`, { method: 'DELETE' })

/** Where an item of the business map was read: code (path:line), a document, an issue, a merge request or a wiki page. */
export interface BusinessSource {
  kind: 'code' | 'doc' | 'issue' | 'mr' | 'wiki'
  ref: string
}

/** What one project's software does for the business: each part's items with the sources they were read from. */
export interface BusinessProfile {
  capabilities: { name: string; description: string; sources: BusinessSource[] }[]
  entities: { name: string; meaning: string; sources: BusinessSource[] }[]
  rules: { rule: string; why: string; sources: BusinessSource[] }[]
  processes: { name: string; steps: string[]; sources: BusinessSource[] }[]
  integrations: { system: string; how: string; sources: BusinessSource[] }[]
  glossary: { term: string; meaning: string; sources: BusinessSource[] }[]
}

/** A project on the business map: its name, domain and summary, while it is mapped its step, and how much each part holds. */
export interface BusinessSystem {
  id: string
  project: string
  name: string
  domain: string | null
  summary: string | null
  status: 'building' | 'ready' | 'failed'
  step: string | null
  error: string | null
  model: string | null
  builtAt: string | null
  counts: { capabilities: number; entities: number; rules: number; processes: number; integrations: number; glossary: number } | null
}

export const businessQuery = {
  queryKey: ['code', 'business'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ argus: boolean; systems: BusinessSystem[] }>('/api/code/business', { signal }),
  // While one is mapped: read again for its step and its end.
  refetchInterval: (q: { state: { data?: { systems: BusinessSystem[] } } }) => (q.state.data?.systems.some((s) => s.status === 'building') ? 2000 : false),
}

export const businessSystemQuery = (id: string) => ({
  queryKey: ['code', 'business', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ system: BusinessSystem; profile: BusinessProfile; web: string | null }>(`/api/code/business/${id}`, { signal }),
  refetchInterval: (q: { state: { data?: { system: BusinessSystem } } }) => (q.state.data?.system.status === 'building' ? 2000 : false),
})

export const mapProject = (project: string) => api<BusinessSystem>('/api/code/business', { body: { project } })
export const mapAgain = (id: string) => api<BusinessSystem>(`/api/code/business/${id}/again`, { body: {} })
export const removeMap = (id: string) => api(`/api/code/business/${id}`, { method: 'DELETE' })

/** Where a source opens in GitLab: the file at its line on the default branch, the issue, the merge request or the wiki page. */
export function sourceUrl(web: string | null, s: BusinessSource): string | null {
  if (!web) return null
  if (s.kind === 'issue') return `${web}/-/issues/${s.ref.replace(/^#/, '')}`
  if (s.kind === 'mr') return `${web}/-/merge_requests/${s.ref.replace(/^!/, '')}`
  if (s.kind === 'wiki') return `${web}/-/wikis/${encodeURIComponent(s.ref)}`
  const m = /^(.*?)(?::(\d+))?$/.exec(s.ref)!
  return `${web}/-/blob/HEAD/${m[1]!.split('/').map(encodeURIComponent).join('/')}${m[2] ? `#L${m[2]}` : ''}`
}

/** How a source reads on its chip: a file with its line, #12, !34, a wiki page. */
export function sourceLabel(s: BusinessSource): string {
  if (s.kind === 'issue') return `#${s.ref.replace(/^#/, '')}`
  if (s.kind === 'mr') return `!${s.ref.replace(/^!/, '')}`
  if (s.kind === 'wiki') return `wiki: ${s.ref}`
  return s.ref
}

/** Security reviews of a repository (Code Studio → Security). */
export interface ScanBrief {
  id: string
  project: string
  ref: string | null
  status: 'queued' | 'running' | 'done' | 'failed'
  step: string | null
  candidates: number
  read: number
  dropped: number
  /** Findings decided in an earlier review of the repository (not a weakness, or a risk accepted until its date), kept so. */
  remembered: number
  dependencies: number
  advisoriesAsOf: string | null
  /** When its packages were last checked again, against advisories renewed since. */
  recheckedAt: string | null
  sbom: boolean
  /** The commit read, once Argus has said which. */
  commit: string | null
  /** The security scanners' run: each tool, and why none ran; null when none are installed. */
  scanners: {
    tools: { name: string; version: string | null; ran: boolean; seconds: number; findings: number; error: string | null }[]
    history?: boolean
    found?: number
    error?: string
  } | null
  createdAt: string
  finishedAt: string | null
  error: string | null
}

export interface SecurityFinding {
  id: string
  kind: 'code' | 'dependency' | 'secret' | 'config'
  /** What found it: the model's review ("model") and the scanners (opengrep, bandit, gitleaks, trivy). */
  foundBy: string[]
  rule: string | null
  /** A secret in the history: the commit it is in. */
  commit: string | null
  exploited: boolean
  path: string
  line: number
  sinkCode: string
  cwe: string
  cweTitle: string
  severity: 'critical' | 'high' | 'medium' | 'low'
  title: string
  source: string | null
  flow: string
  exploit: string
  fix: string
  pages: { title: string; url: string | null }[]
  verified: boolean | null
  verifyNote: string | null
  state: 'open' | 'fixed' | 'accepted' | 'wrong'
  decidedAt: string | null
  why: string | null
  until: string | null
  /** When it was found: the review's end, or a later check of the packages. */
  foundAt: string
}

export interface Scan {
  scan: ScanBrief
  summary: string | null
  model: string | null
  by: string
  mine: boolean
  findings: SecurityFinding[]
}

const running = (s: ScanBrief) => s.status === 'running' || s.status === 'queued'

export const securityScansQuery = {
  queryKey: ['code', 'security'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ scan: ScanBrief; mine: boolean; open: Partial<Record<SecurityFinding['severity'], number>> }[]>('/api/code/security', { signal }),
  refetchInterval: (q: { state: { data?: { scan: ScanBrief }[] } }) => (q.state.data?.some((s) => running(s.scan)) ? 3000 : false),
}

export const securityScanQuery = (id: string) => ({
  queryKey: ['code', 'security', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Scan>(`/api/code/security/${id}`, { signal }),
  refetchInterval: (q: { state: { data?: Scan } }) => (q.state.data && running(q.state.data.scan) ? 2000 : false),
})
