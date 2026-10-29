import { api } from '@/lib/api'

export type Detector = 'secrets' | 'pii' | 'words' | 'topic' | 'injection' | 'links' | 'files' | 'commands' | 'exfiltration'
export type Stage = 'question' | 'file' | 'answer' | 'tool_call' | 'tool_result' | 'api'
export type RuleAction = 'block' | 'mask' | 'warn' | 'justify' | 'log' | 'reroute' | 'allow'

export interface SafeguardRule {
  id: string
  name: string
  description: string | null
  order: number
  enabled: boolean
  monitor: boolean
  detector: Detector
  stages: Stage[]
  groups: string[]
  people: string[]
  models: string[]
  action: RuleAction
  message: string | null
  rerouteModel: string | null
  strike: boolean
  kinds: string[]
  patterns: string | null
  topics: string | null
  categories: string[]
  domains: string[]
  domainsAllowed: boolean
  extensions: string[]
  maxFileMb: number | null
  modelCheck: boolean
  updatedAt: string
}

export type RuleDraft = Omit<SafeguardRule, 'id' | 'order' | 'updatedAt'>

export interface RulesResponse {
  rules: { rule: SafeguardRule; week: number }[]
  detectors: Detector[]
  stages: Stage[]
  actions: RuleAction[]
  categories: { key: string; description: string }[]
  secretKinds: string[]
  piiKinds: string[]
  commandKinds: string[]
}

export interface RuleHit {
  ruleId: string
  rule: string
  detector: Detector
  outcome: string
  detail: string
  monitor: boolean
}

export interface TrialResult {
  outcome: 'blocked' | 'asks' | 'masked' | 'passes'
  text: string
  reason: string | null
  reroute: string | null
  hits: RuleHit[]
}

export interface SafeguardEvent {
  id: string
  ruleId: string | null
  ruleName: string
  stage: Stage
  detector: Detector
  outcome: string
  monitor: boolean
  detail: string | null
  excerpt: string | null
  justification: string | null
  model: string | null
  conversationId: string | null
  at: string
  person: { id: string; name: string | null } | null
  review: 'asked' | 'allowed' | 'confirmed' | null
  reviewReason: string | null
  reviewNote: string | null
  reviewedAt: string | null
  reviewedBy: string | null
  allowedUntil: string | null
}

export interface SafeguardStats {
  days: { day: string; outcomes: Record<string, number>; monitor: number }[]
  rules: { id: string | null; name: string; count: number; blocked: number; monitor: number }[]
  people: number
  waiting: number
}

export const rulesQuery = {
  queryKey: ['admin', 'safeguards', 'rules'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<RulesResponse>('/api/admin/safeguards/rules', { signal }),
}

export const templatesQuery = {
  queryKey: ['admin', 'safeguards', 'templates'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<(Partial<RuleDraft> & { name: string; description: string })[]>('/api/admin/safeguards/templates', { signal }),
  staleTime: Infinity,
}

export const eventsQuery = (filters: Record<string, string>) => ({
  queryKey: ['admin', 'safeguards', 'events', filters] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SafeguardEvent[]>(`/api/admin/safeguards/events?${new URLSearchParams(filters)}`, { signal }),
})

export const statsQuery = {
  queryKey: ['admin', 'safeguards', 'stats'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SafeguardStats>('/api/admin/safeguards/stats?days=30', { signal }),
}

export const detectorText: Record<Detector, { title: string; about: string }> = {
  secrets: { title: 'Secrets', about: 'Private keys, cloud and service tokens, passwords.' },
  pii: { title: 'Personal data', about: 'E-mail addresses, phone and card numbers, IBANs, IP and MAC addresses, national, passport, social security and insurance numbers, wallet addresses.' },
  words: { title: 'Words and patterns', about: 'Words, phrases and re: patterns, matched as whole words.' },
  topic: { title: 'Topics', about: 'Subjects the model judges: built-in harms, or topics you describe.' },
  injection: { title: 'Prompt injection', about: 'Attempts to get round the assistant’s rules in any language or disguise, encoded or hidden from people, and orders in what tools bring.' },
  links: { title: 'Links', about: 'Links to domains refused, or to any domain not on a list.' },
  files: { title: 'Files', about: 'Attached files by type and size.' },
  commands: { title: 'Dangerous commands', about: 'Commands that wipe files or disks, open a reverse shell, run a script from the internet, drop a database or turn security off: for what agents send to tools.' },
  exfiltration: { title: 'Data sent out', about: 'Images and links that would send data to another site, the way injected instructions leak what the model read.' },
}

export const stageText: Record<Stage, string> = {
  question: 'Questions',
  file: 'Attached files',
  answer: 'Answers',
  tool_call: 'What goes to tools',
  tool_result: 'What tools bring back',
  api: 'API keys’ requests',
}

export const actionText: Record<RuleAction, { title: string; about: string }> = {
  block: { title: 'Block', about: 'Refused; the person is told why and may ask for a review.' },
  mask: { title: 'Mask', about: 'What it found is replaced by a marker (secrets are never stored).' },
  warn: { title: 'Warn', about: 'The person is asked to confirm before it is sent.' },
  justify: { title: 'Ask for a reason', about: 'The person gives a reason, which is recorded.' },
  log: { title: 'Log only', about: 'It goes; the event is recorded.' },
  reroute: { title: 'Send to another model', about: 'The question goes to the model you name (the company’s own, say).' },
  allow: { title: 'Allow', about: 'Exempts what it covers from the rules below it of the same detector.' },
}

export const emptyRule: RuleDraft = {
  name: '', description: null, enabled: true, monitor: false, detector: 'words', stages: ['question'], groups: [], people: [], models: [], action: 'block',
  message: null, rerouteModel: null, strike: false, kinds: [], patterns: null, topics: null, categories: [], domains: [], domainsAllowed: false, extensions: [],
  maxFileMb: null, modelCheck: false,
}
