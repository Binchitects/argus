import { api } from '@/lib/api'

/** Code Arena's goal for its session, as Arena keeps it with the chat (goal mode). */
export interface ChatCodeGoal {
  text: string
  check: { kind: 'command' | 'condition'; text: string; by: 'person' | 'agent' | null } | null
  budget: { steps: number; minutes: number; tokens: number }
  status: 'working' | 'paused' | 'met' | 'budget' | 'stuck' | 'cancelled'
  turns: number
  steps: number
  tokens: number
  ms: number
  passed: boolean | null
  said: string | null
  checkedAt: string | null
  outcome: string | null
  startedAt: string
  updatedAt: string
}

export const codeGoalQuery = (chat: string) => ({
  queryKey: ['chat', 'conversations', chat, 'goal'] as const,
  // None: 204, no body.
  queryFn: async ({ signal }: { signal: AbortSignal }) => (await api<ChatCodeGoal | undefined>(`/api/chat/conversations/${chat}/goal`, { signal })) ?? null,
  // While Code Arena works on it: read again every few seconds.
  refetchInterval: (q: { state: { data?: ChatCodeGoal | null } }) => (q.state.data?.status === 'working' ? 5000 : false),
})
