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

export interface ComplianceView {
  frameworks: Record<string, string>
  controls: Control[]
  checks: CheckResult[]
  checkedAt: string
}

export const FRAMEWORKS = [
  { key: 'iso27001', label: 'ISO 27001' },
  { key: 'soc2', label: 'SOC 2' },
  { key: 'hipaa', label: 'HIPAA' },
  { key: 'gdpr', label: 'GDPR' },
] as const

export type Framework = (typeof FRAMEWORKS)[number]['key']

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
