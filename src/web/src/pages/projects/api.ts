import { api } from '@/lib/api'

export type ProjectRole = 'owner' | 'contributor' | 'viewer'

export interface ProjectSummary {
  id: string
  name: string
  description: string
  global: boolean
  status: 'active' | 'archived'
  startsOn: string | null
  dueOn: string | null
  monthlyBudget: number | null
  role: ProjectRole
  owner: string | null
  /** Made by the person, or they (or their groups) are in it; a global one they are not in is not. */
  mine: boolean
  members: number
  chats: number
  goals: number
  goalsDone: number
  spentThisMonth: number
  updatedAt: string
}

export interface ProjectMemberRow {
  id: string
  role: ProjectRole
  userId: string | null
  groupId: string | null
  name: string
  email: string | null
  addedAt: string
}

export type GoalStatus = 'planned' | 'doing' | 'done' | 'dropped'

export interface ProjectGoal {
  id: string
  title: string
  description: string | null
  status: GoalStatus
  dueOn: string | null
  doneAt: string | null
  position: number
}

export interface ProjectTask {
  id: string
  title: string
  goalId: string | null
  assigneeId: string | null
  assignee: string | null
  done: boolean
  doneAt: string | null
  createdAt: string
}

export type ItemKind = 'assistant' | 'skill' | 'app'

export interface Project extends Omit<ProjectSummary, 'mine' | 'members' | 'chats' | 'goals' | 'goalsDone' | 'spentThisMonth'> {
  alertPercent: number
  stopAtBudget: boolean
  createdAt: string
  ownerId: string
  isOwner: boolean
  members: ProjectMemberRow[]
  repositories: { path: string; host: string | null; addedAt: string }[]
  items: { kind: ItemKind; id: string; name: string; addedAt: string; mine: boolean }[]
  /** The person's own chats in it, and the rooms they are in. */
  chats: { id: string; title: string; kind: 'chat' | 'room' | 'session'; place: string | null; updatedAt: string; mine: boolean }[]
  /** The others' chats, only counted by whose they are. */
  otherChats: { person: string; count: number }[]
  goals: ProjectGoal[]
  tasks: ProjectTask[]
}

export interface ProjectPerson {
  id: string | null
  name: string
  email: string | null
  requests: number
  tokens: number
  cost: number
  agentSeconds: number
  activeSeconds: number
  messages: number
}

export interface ProjectUse {
  name: string
  requests: number
  tokens: number
  cost: number
}

export interface ProjectReport {
  from: string
  to: string
  intervalMs: number
  totals: { requests: number; input: number; cached: number; output: number; cost: number; agentSeconds: number; activeSeconds: number; people: number; messages: number }
  progress: { goals: number; goalsDone: number; tasks: number; tasksDone: number; tasksDoneInRange: number; steps: number; stepsDone: number; chats: number; rooms: number; sessions: number }
  models: ProjectUse[]
  surfaces: ProjectUse[]
  series: { at: string; input: number; output: number; cost: number }[]
  people: ProjectPerson[]
  /** Each person's share is for the owners: the others get their own row. */
  peopleHidden: boolean
  budget: { monthly: number; spent: number; percent: number; alertPercent: number; stopAtBudget: boolean } | null
  merged: { repository: string; iid: number; title: string; author: string | null; mergedAt: string | null; url: string | null }[]
  mergedError: string | null
}

export const projectsQuery = {
  queryKey: ['projects'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ProjectSummary[]>('/api/projects', { signal }),
}

export const projectQuery = (id: string) => ({
  queryKey: ['projects', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Project>(`/api/projects/${id}`, { signal }),
})

export const roleLabels: Record<ProjectRole, string> = { owner: 'Owner', contributor: 'Contributor', viewer: 'Viewer' }

export const goalLabels: Record<GoalStatus, string> = { planned: 'Planned', doing: 'Doing', done: 'Done', dropped: 'Dropped' }

export const mayAdd = (role: ProjectRole | undefined) => role === 'owner' || role === 'contributor'

/** Seconds as people read them: 12 min, 3 h 05 min. */
export function hours(seconds: number): string {
  const minutes = Math.round(seconds / 60)
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`
}

/** A project's progress, 0 to 100: its goals done, else its tasks done; null when it has neither. */
export function progressOf(p: { goals: number; goalsDone: number; tasks?: number; tasksDone?: number }): number | null {
  if (p.goals > 0) return Math.round((100 * p.goalsDone) / p.goals)
  if (p.tasks) return Math.round((100 * (p.tasksDone ?? 0)) / p.tasks)
  return null
}
