import { api } from '@/lib/api'

export type State = 'ok' | 'warn' | 'fail' | 'unknown'

export interface CheckResult {
  id: string
  title: string
  state: State
  detail: string
}

export interface Attestation {
  actor: string
  at: string
  note: string
  /** A year after it: the company's part is due again then. */
  due: string
}

export interface Control {
  id: string
  title: string
  meets: string
  frameworks: Record<string, string[]>
  docs: string[]
  tests: string[]
  checks: string[]
  operator: string | null
  /** The worst of its checks inside the app; null when it has none. */
  state: State | null
  attested: Attestation | null
}

/** A framework's catalog: its clauses, and how many of them a control serves. */
export interface Coverage {
  title: string
  clauses: number
  served: number
}

export interface ComplianceView {
  frameworks: Record<string, string>
  coverage: Record<string, Coverage>
  controls: Control[]
  checks: CheckResult[]
  checkedAt: string
  /** How long each kind of data and record is kept, and what removes it. */
  retention?: { what: string; rule: string }[]
}

export const FRAMEWORKS = [
  { key: 'iso27001', label: 'ISO 27001', group: 'Security' },
  { key: 'soc2', label: 'SOC 2', group: 'Security' },
  { key: 'nistcsf', label: 'NIST CSF 2.0', group: 'Security' },
  { key: 'nist80053', label: 'NIST 800-53', group: 'Security' },
  { key: 'nist800171', label: 'NIST 800-171', group: 'Security' },
  { key: 'pcidss', label: 'PCI DSS', group: 'Security' },
  { key: 'csaccm', label: 'CSA STAR', group: 'Security' },
  { key: 'hipaa', label: 'HIPAA', group: 'Privacy' },
  { key: 'gdpr', label: 'GDPR', group: 'Privacy' },
  { key: 'iso42001', label: 'ISO 42001', group: 'AI' },
  { key: 'euaiact', label: 'EU AI Act', group: 'AI' },
  { key: 'airmf', label: 'NIST AI RMF', group: 'AI' },
  { key: 'iso22301', label: 'ISO 22301', group: 'Continuity' },
  { key: 'commoncriteria', label: 'Common Criteria', group: 'Product evaluation' },
] as const

export const FRAMEWORK_GROUPS = [...new Set(FRAMEWORKS.map((f) => f.group))]

export type Framework = (typeof FRAMEWORKS)[number]['key']

/** How ready for a framework: its score, its controls holding and attested, and what is missing where. */
export interface FrameworkReadiness {
  framework: string
  title: string
  score: number
  controls: number
  holding: number
  attested: number
  gaps: { what: string; where: string }[]
}

export const readinessQuery = {
  queryKey: ['admin', 'compliance', 'readiness'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<FrameworkReadiness[]>('/api/admin/compliance/readiness', { signal }),
}

export const complianceQuery = {
  queryKey: ['admin', 'compliance'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ComplianceView>('/api/admin/compliance', { signal }),
}

/** A control's document as a page of the manual: authentication.md#x is /help/authentication#x, compliance/README.md is /help/compliance. */
export function manualLink(doc: string): string {
  const [file = '', anchor] = doc.split('#')
  const id = file.replace(/(^|\/)README\.md$/, '').replace(/\.md$/, '')
  return `/help/${id}${anchor ? `#${anchor}` : ''}`
}

/** Whether the company's part was attested and that still counts. */
export const attestedNow = (c: Control, now: Date) => c.attested !== null && new Date(c.attested.due) > now

/** For a framework's tile: the controls that serve it, by state, and how many of them the company has attested. */
export function tally(controls: Control[], framework: Framework, now: Date) {
  const serving = controls.filter((c) => (c.frameworks[framework]?.length ?? 0) > 0)
  const count = (s: State) => serving.filter((c) => c.state === s).length
  return {
    controls: serving.length,
    clauses: new Set(serving.flatMap((c) => c.frameworks[framework] ?? [])).size,
    ok: count('ok'),
    warn: count('warn'),
    fail: count('fail'),
    attested: serving.filter((c) => attestedNow(c, now)).length,
  }
}

export interface ReviewSummary {
  id: string
  title: string
  createdAt: string
  createdBy: string
  dueAt: string
  closedAt: string | null
  closedBy: string | null
  items: number
  kept: number
  revoked: number
}

export type ReviewKind = 'role' | 'member' | 'unused'

export interface ReviewItem {
  id: string
  kind: ReviewKind
  subject: string
  what: string
  decision: 'keep' | 'revoke' | null
  decidedBy: string | null
  decidedAt: string | null
  note: string | null
  /** What revoking did, or why it could not be done here. */
  outcome: string | null
}

export interface ReviewDetail {
  review: Omit<ReviewSummary, 'items' | 'kept' | 'revoked'>
  items: ReviewItem[]
}

export const reviewsQuery = {
  queryKey: ['admin', 'compliance', 'reviews'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ReviewSummary[]>('/api/admin/compliance/reviews', { signal }),
}

export const reviewQuery = (id: string) => ({
  queryKey: ['admin', 'compliance', 'reviews', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ReviewDetail>(`/api/admin/compliance/reviews/${id}`, { signal }),
})

/** Where a review stands: closed, late (past its due date and open), or under way. */
export function reviewState(r: { closedAt: string | null; dueAt: string }, now: Date): 'closed' | 'late' | 'open' {
  if (r.closedAt) return 'closed'
  return new Date(r.dueAt) < now ? 'late' : 'open'
}

export type DataRequestKind = 'access' | 'portability' | 'rectification' | 'restriction' | 'objection' | 'erasure'

export interface DataRequest {
  id: string
  /** Null once they were erased. */
  userId: string | null
  person: string
  email: string | null
  kind: DataRequestKind
  details: string | null
  status: 'open' | 'done' | 'refused'
  receivedAt: string
  dueAt: string
  receivedBy: string
  doneAt: string | null
  doneBy: string | null
  outcome: string | null
  /** An erasure's certificate is there to download. */
  certificate: boolean
}

/** Each kind as people ask for it, and what it means. */
export const DATA_REQUEST_KINDS: { kind: DataRequestKind; label: string; about: string }[] = [
  { kind: 'access', label: 'A copy of my data', about: 'Everything kept about you, and how it is used.' },
  { kind: 'portability', label: 'My data to take elsewhere', about: 'Your data in a form another service can read.' },
  { kind: 'rectification', label: 'A correction', about: 'Something kept about you is wrong: say what.' },
  { kind: 'restriction', label: 'Restrict its use', about: 'Keep your data, but learn nothing more from it (memory, digests, skills).' },
  { kind: 'objection', label: 'Object to its use', about: 'Stop using your data for what you object to: say what.' },
  { kind: 'erasure', label: 'Erase my data', about: 'Everything of yours deleted: your account too.' },
]

export const dataRequestLabel = (kind: DataRequestKind) => DATA_REQUEST_KINDS.find((k) => k.kind === kind)?.label ?? kind

export const dataRequestsQuery = {
  queryKey: ['admin', 'compliance', 'data-requests'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DataRequest[]>('/api/admin/compliance/data-requests', { signal }),
}

export interface SecurityAlert {
  id: string
  kind: string
  title: string
  detail: string | null
  at: string
  incidentId: string | null
}

export interface Incident {
  id: string
  title: string
  summary: string | null
  severity: 'low' | 'medium' | 'high' | 'critical'
  status: 'open' | 'contained' | 'resolved' | 'closed'
  detectedAt: string
  openedBy: string
  personalData: boolean
  healthData: boolean
  authorityDecidedAt: string | null
  authorityNotified: boolean | null
  peopleNotifiedAt: string | null
  closedAt: string | null
  lessons: string | null
  /** The GDPR's 72 hours to tell the supervisory authority (personal data); null: not asked. */
  authorityDue: string | null
  /** HIPAA's 60 days to tell the people (health data); null: not asked. */
  peopleDue: string | null
}

export interface IncidentStep {
  id: string
  at: string
  by: string
  kind: string
  text: string | null
}

export const incidentsQuery = {
  queryKey: ['admin', 'compliance', 'incidents'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ alerts: SecurityAlert[]; incidents: Incident[] }>('/api/admin/compliance/incidents', { signal }),
}

export const incidentQuery = (id: string) => ({
  queryKey: ['admin', 'compliance', 'incidents', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ incident: Incident; events: IncidentStep[]; alerts: SecurityAlert[] }>(`/api/admin/compliance/incidents/${id}`, { signal }),
})

/** Each step as the timeline says it. */
export const STEP_WORDS: Record<string, string> = {
  opened: 'Opened',
  note: 'Note',
  contained: 'Contained',
  resolved: 'Resolved',
  authority_notified: 'Supervisory authority told',
  authority_not_required: 'Authority need not be told',
  people_notified: 'The people affected told',
  closed: 'Closed',
}
