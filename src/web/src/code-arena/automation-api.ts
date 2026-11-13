import { api } from './http'

/** One of Code Arena's own schedules: a prompt run in a folder every so often, as a session of its own. */
export interface LocalSchedule {
  name: string
  prompt: string
  folder: string
  every: string
  at: string | null
  mode: 'plan' | 'ask' | 'auto-edit' | 'yolo'
  model: string | null
  enabled: boolean
  lastAt: string | null
  lastStatus: string | null
  lastLog: string | null
  next: string
  when: string
}

export interface ArenaAgent {
  id: string
  name: string
  description: string | null
  enabled: boolean
  mine: boolean
  owner: string | null
  trigger: string
}

export interface ArenaTask {
  id: string
  name: string
  cron: string
  enabled: boolean
  running: boolean
  lastRun: { status: string; startedAt: string } | null
}

export interface Automation {
  schedules: LocalSchedule[]
  folder: string
  agents: ArenaAgent[] | null
  agentsProblem: string | null
  tasks: ArenaTask[] | null
  tasksProblem: string | null
}

export interface AgentRun {
  workflow: string
  status: string
  error: string | null
  steps: { name: string | null; kind: string; status: string; output: unknown }[]
}

export const automationQuery = {
  queryKey: ['code', 'automation'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Automation>('/api/automation', { signal }),
  // A schedule running shows how it ends.
  refetchInterval: (q: { state: { data?: Automation } }) => (q.state.data?.schedules.some((s) => s.lastStatus === 'running') ? 3000 : 30_000),
}

