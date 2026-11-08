import type { AgentWork } from '@/pages/chat/types'
import { api } from './http'

/** A sub-agent of the agent's (a task call) or a background task of the person's, as the Tasks panel lists it. */
export interface AgentTask {
  /** t1, t2…: its number in this run. */
  id: string
  kind: 'subagent' | 'background'
  title: string
  status: 'queued' | 'running' | 'done' | 'failed' | 'stopped'
  /** The task call it answers (the chat's line of it); null for the person's own. */
  call: string | null
  /** The assistant (or own sub-agent) it works as. */
  as: string | null
  model: string
  /** It may change files: such tasks run one at a time. */
  writes: boolean
  created: string
  started: string | null
  ended: string | null
  /** Its time so far, or in all once over. */
  ms: number | null
  tokens: { prompt: number; cached: number; completion: number }
  /** Its tool calls so far. */
  calls: number
  /** What it does now, in a few words; null once over. */
  doing: string | null
  error: string | null
  /** Kept from an earlier run of the session: read only. */
  kept: boolean
}

/** One task, whole: what it was asked, its tools, its report, and its transcript (as the chat shows a sub-agent's work). */
export interface AgentTaskDetail extends AgentTask {
  prompt: string
  tools: string[]
  report: string | null
  work: AgentWork | null
}

export interface AgentTaskList {
  tasks: AgentTask[]
  running: number
  mostAtOnce: number
}

/** 12345 → "12.3k". */
export function tokens(n: number): string {
  if (n < 1000) return String(n)
  if (n < 100_000) return `${(n / 1000).toFixed(1).replace(/\.0$/, '')}k`
  if (n < 1_000_000) return `${Math.round(n / 1000)}k`
  return `${(n / 1_000_000).toFixed(2).replace(/\.?0+$/, '')}M`
}

/** Over: it ran and ended, however. */
export const taskOver = (t: Pick<AgentTask, 'status'>) => t.status === 'done' || t.status === 'failed' || t.status === 'stopped'

export const agentTasksQuery = {
  queryKey: ['code', 'agent-tasks'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AgentTaskList>('/api/agent-tasks', { signal }),
  // While any is at work: read every second (its time, tokens and what it does move).
  refetchInterval: (q: { state: { data?: AgentTaskList } }) => (q.state.data?.running ? 1000 : false),
}

export const agentTaskQuery = (id: string) => ({
  queryKey: ['code', 'agent-tasks', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AgentTaskDetail>(`/api/agent-tasks/${encodeURIComponent(id)}`, { signal }),
  refetchInterval: (q: { state: { data?: AgentTaskDetail } }) => (q.state.data && !taskOver(q.state.data) ? 1000 : false),
})

export const startAgentTask = (body: { prompt: string; title?: string; assistant?: string; model?: string; edits?: boolean }) => api<AgentTask>('/api/agent-tasks', { body })
export const stopAgentTask = (id: string) => api(`/api/agent-tasks/${encodeURIComponent(id)}/stop`, { body: {} })

/** How the goal is checked: a command that succeeds once it is met, or a condition a fresh reader looks at. */
export interface GoalCheck {
  kind: 'command' | 'condition'
  text: string
  /** person: set with the goal; agent: written down by the agent first. */
  by?: 'person' | 'agent' | null
}

export interface GoalBudget {
  steps: number
  minutes: number
  tokens: number
  /** No limit but its being met (or stuck, or stopped): a very long session. */
  untilMet?: boolean
}

/** The session's goal (goal mode), as it stands. */
export interface CodeGoal {
  text: string
  check: GoalCheck | null
  budget: GoalBudget
  status: 'working' | 'paused' | 'met' | 'budget' | 'stuck' | 'cancelled' | 'queued'
  turns: number
  steps: number
  tokens: number
  /** The time worked on it (paused time not counted). */
  ms: number
  started: string
  updated: string
  /** The last check: whether it passed and what it said. */
  passed: boolean | null
  said: string | null
  checkedAt: string | null
  /** How it ended: how it was checked, or why it stopped. */
  outcome: string | null
}

export const goalDefaults: GoalBudget = { steps: 200, minutes: 60, tokens: 5_000_000 }
export const goalBounds = { steps: 100_000, minutes: 30 * 24 * 60, tokens: 10_000_000_000 } as const

export const goalActive = (g: Pick<CodeGoal, 'status'> | null | undefined) => g?.status === 'working' || g?.status === 'paused'

export const setGoal = (body: { text: string; check?: GoalCheck | null; budget: GoalBudget }) => api<{ goal: CodeGoal }>('/api/goal', { body })
export const pauseGoal = () => api<{ goal: CodeGoal }>('/api/goal/pause', { body: {} })
export const resumeGoal = () => api<{ goal: CodeGoal }>('/api/goal/resume', { body: {} })
export const cancelGoal = () => api<{ goal: CodeGoal }>('/api/goal', { method: 'DELETE', body: {} })
/** The first goal waiting, started now (the one before ended). */
export const nextGoal = () => api<{ goal: CodeGoal }>('/api/goal/next', { body: {} })
/** A waiting goal dropped, by its place from 1. */
export const dropGoal = (place: number) => api<{ goal: CodeGoal }>('/api/goal/drop', { body: { place } })

/** A prompt run as a background agent every so often, while the session is open. */
export interface CodeLoop {
  id: string
  prompt: string
  everyMs: number
  edits: boolean
  runs: number
  skipped: number
  nextAt: string
  lastAt: string | null
  lastTask: string | null
  lastStatus: string | null
}

export const loopsQuery = {
  queryKey: ['code', 'loops'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ loops: CodeLoop[] }>('/api/loops', { signal }),
  refetchInterval: 10_000,
}
export const startLoop = (body: { every: string; prompt: string; edits: boolean }) => api<{ loops: CodeLoop[] }>('/api/loops', { body })
export const stopLoop = (id: string) => api(`/api/loops/${id}`, { method: 'DELETE', body: {} })

/** One of the person's assistants in Arena, as Code Arena works as it. */
export interface CodeAssistant {
  id: string
  name: string
  description: string | null
  instructions: string | null
  model: string | null
  thinking: string | null
  tools: string[] | null
  skills: string[]
  icon: string
  color: string
}

export const assistantsQuery = {
  queryKey: ['code', 'assistants'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ assistants: CodeAssistant[] | null; current: string | null }>('/api/assistants', { signal }),
  staleTime: 30_000,
}

export const chooseAssistant = (id: string | null) => api<{ said: string }>('/api/assistant', { body: id ? { id } : {} })
