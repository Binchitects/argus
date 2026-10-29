import { api } from '@/lib/api'

export type TriggerKind = 'manual' | 'schedule' | 'webhook' | 'gitlab' | 'room' | 'form'
export type StepKind =
  | 'ask' | 'agent' | 'tool' | 'code' | 'data' | 'knowledge' | 'http' | 'condition' | 'loop' | 'approval' | 'wait' | 'stop' | 'text' | 'room' | 'email' | 'notify' | 'comment' | 'gitlab' | 'file'

export interface FlowField {
  name: string
  label: string
  kind: 'text' | 'long' | 'choice'
  choices?: string[] | null
  required?: boolean
}

export interface FlowTrigger {
  kind: TriggerKind
  cron?: string | null
  timeZone?: string
  events?: string[] | null
  /** GitLab: the branches it takes (globs, comma separated); empty: every branch. */
  branches?: string | null
  /** GitLab: labels a merge request or issue has one of; empty: any. */
  labels?: string | null
  roomId?: string | null
  keyword?: string | null
  fields?: FlowField[] | null
}

export interface FlowStep {
  id: string
  kind: StepKind
  name?: string | null
  prompt?: string | null
  system?: string | null
  json?: boolean
  model?: string | null
  tools?: string[] | null
  tool?: string | null
  function?: string | null
  arguments?: Record<string, string> | null
  left?: string | null
  op?: string | null
  right?: string | null
  then?: FlowStep[] | null
  else?: FlowStep[] | null
  over?: string | null
  most?: number | null
  steps?: FlowStep[] | null
  question?: string | null
  roomId?: string | null
  text?: string | null
  subject?: string | null
  fileName?: string | null
  /** gitlab: comment, label or issue. */
  action?: 'comment' | 'label' | 'issue' | null
  project?: string | null
  target?: string | null
  labels?: string | null
  url?: string | null
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE' | null
  sourceId?: string | null
  sql?: string | null
  minutes?: number | null
  outcome?: 'done' | 'failed' | null
}

export interface Flow {
  trigger: FlowTrigger
  steps: FlowStep[]
}

export type Reach = 'private' | 'groups' | 'company'

export interface WorkflowBrief {
  id: string
  name: string
  description: string
  enabled: boolean
  reach: Reach
  groupIds: string[]
  nextRunAt: string | null
  lastRunAt: string | null
  updatedAt: string
  mine: boolean
  owner: string | null
  trigger: TriggerKind
}

export interface Workflow {
  id: string
  name: string
  description: string
  enabled: boolean
  reach: Reach
  groupIds: string[]
  nextRunAt: string | null
  lastRunAt: string | null
  mine: true
  flow: Flow
  hook: string
  secretSet: boolean
}

/** A workflow someone shared: what it is, and its form's fields. */
export interface SharedWorkflow {
  id: string
  name: string
  description: string
  enabled: boolean
  mine: false
  owner: string | null
  trigger: TriggerKind
  fields: FlowField[]
}

export type RunStatus = 'running' | 'waiting' | 'done' | 'failed' | 'stopped'

export interface RunBrief {
  id: string
  trigger: string
  status: RunStatus
  error: string | null
  startedAt: string
  finishedAt: string | null
  steps: number
}

export interface RunStep {
  path: string
  stepId: string
  kind: StepKind
  name: string
  status: 'running' | 'waiting' | 'done' | 'failed' | 'skipped'
  input: string | null
  output: string | null
  link: string | null
  error: string | null
  startedAt: string
  finishedAt: string | null
}

export interface Run {
  id: string
  workflowId: string
  workflow: string
  trigger: string
  status: RunStatus
  error: string | null
  startedAt: string
  finishedAt: string | null
  input: Record<string, unknown>
  flow: Flow | null
  steps: RunStep[]
}

export interface Template {
  key: string
  name: string
  description: string
  flow: Flow
}

export interface StudioTool {
  id: string
  title: string
  description: string
  icon: string
}

export interface ToolFunction {
  name: string
  description: string | null
  parameters: { name: string; description: string | null }[] | null
}

export const workflowsQuery = {
  queryKey: ['studio'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<WorkflowBrief[]>('/api/studio', { signal }),
}

export const templatesQuery = {
  queryKey: ['studio', 'templates'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Template[]>('/api/studio/templates', { signal }),
  staleTime: Infinity,
}

export const toolsQuery = {
  queryKey: ['studio', 'tools'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<StudioTool[]>('/api/studio/tools', { signal }),
}

export const functionsQuery = (tool: string) => ({
  queryKey: ['studio', 'tools', tool] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ToolFunction[]>(`/api/studio/tools/${tool}/functions`, { signal }),
})

export const workflowQuery = (id: string) => ({
  queryKey: ['studio', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Workflow | SharedWorkflow>(`/api/studio/${id}`, { signal }),
})

export const runsQuery = (id: string) => ({
  queryKey: ['studio', id, 'runs'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<RunBrief[]>(`/api/studio/${id}/runs`, { signal }),
})

export const runQuery = (run: string) => ({
  queryKey: ['studio', 'runs', run] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Run>(`/api/studio/runs/${run}`, { signal }),
})

export const createWorkflow = (body: { name: string; description?: string; flow: Flow; enabled?: boolean }) =>
  api<{ workflow: Workflow; secret: string | null }>('/api/studio', { body })

export const saveWorkflow = (id: string, body: Partial<{ name: string; description: string; flow: Flow; enabled: boolean; reach: Reach; groupIds: string[] }>) =>
  api<{ workflow: Workflow; secret: string | null }>(`/api/studio/${id}`, { method: 'PUT', body })

export const runWorkflow = (id: string, body: { text?: string; fields?: Record<string, string> }) => api<{ id: string }>(`/api/studio/${id}/run`, { body })

export const answerApproval = (run: string, approve: boolean) => api(`/api/studio/runs/${run}/approve`, { body: { approve } })

export const stopRun = (run: string) => api(`/api/studio/runs/${run}/stop`, { body: {} })

export const newSecret = (id: string) => api<{ secret: string }>(`/api/studio/${id}/secret`, { body: {} })

export const removeWorkflow = (id: string) => api(`/api/studio/${id}`, { method: 'DELETE' })

export const fromTask = (task: string) => api<{ workflow: Workflow; secret: string | null }>(`/api/studio/from-task/${task}`, { body: {} })

export const stepLabels: Record<StepKind, string> = {
  ask: 'Ask a model',
  agent: 'Agent with tools',
  tool: 'Use a tool',
  code: 'Run Python',
  data: 'Ask a data source',
  knowledge: 'Search company knowledge',
  http: 'Call a web address',
  condition: 'If',
  loop: 'For each',
  approval: 'Ask me first',
  wait: 'Wait',
  stop: 'End the run',
  text: 'Write text',
  room: 'Say in a room',
  email: 'Email me',
  notify: 'Notify me',
  comment: 'Comment in GitLab',
  gitlab: 'Act in GitLab',
  file: 'Save a file',
}

/** GitLab's events a trigger takes, in words. */
export const gitlabEvents: [string, string][] = [
  ['merge_request', 'A merge request opens or gets new commits'],
  ['merge_request_approved', 'A merge request is approved'],
  ['merge_request_merged', 'A merge request is merged'],
  ['merge_request_closed', 'A merge request is closed'],
  ['pipeline_failed', 'A pipeline fails'],
  ['pipeline_succeeded', 'A pipeline passes'],
  ['job_failed', 'A job fails'],
  ['issue', 'An issue opens or reopens'],
  ['issue_closed', 'An issue is closed'],
  ['comment', 'Someone comments'],
  ['push', 'Commits are pushed'],
  ['tag', 'A tag is pushed'],
  ['release', 'A release is made'],
  ['deployment', 'A deployment ends'],
  ['wiki_page', 'A wiki page is made or changed'],
]

export const triggerLabels: Record<TriggerKind, string> = {
  manual: 'By hand',
  schedule: 'On a schedule',
  webhook: 'On a webhook',
  gitlab: 'On GitLab events',
  room: "On a room's message",
  form: 'From a form',
}

export const opLabels: Record<string, string> = {
  contains: 'contains',
  not_contains: 'does not contain',
  equals: 'is',
  not_equals: 'is not',
  greater: 'is more than',
  less: 'is less than',
  empty: 'is empty',
  not_empty: 'is not empty',
}
