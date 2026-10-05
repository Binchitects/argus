import { toolTitle } from './format'
import { partsOf } from './tool-args'
import type { AgentWork, Message } from './types'

/** "2 of 4 parts done": how far a delegate call's sub-agents are. */
export function partsDone(done: number, total: number): string {
  return `${done} of ${total} part${total === 1 ? '' : 's'} done`
}

/**
 * Which step deep research is on, in words, from what the answer has done so far: planning
 * (nothing called yet), researching its parts (sub-agents at work), filling gaps (sub-agents
 * or a tool called after them), writing the report.
 */
export function researchStep(answer: Message[], agents: Record<string, AgentWork[]> | undefined): string {
  const calls = answer.flatMap((m) => (m.role === 'assistant' ? (m.toolCalls ?? []) : []))
  const answered = new Set(answer.filter((m) => m.role === 'tool').map((m) => m.toolCallId))
  const pending = calls.find((c) => !answered.has(c.id))
  // Delegations whose parts ran (a refused one has no work to show): after one, the next research fills gaps.
  const ran = new Set(answer.filter((m) => m.role === 'tool' && m.details?.agents).map((m) => m.toolCallId))
  const delegated = calls.some((c) => c.function.name === 'delegate' && ran.has(c.id))
  if (pending?.function.name === 'delegate') {
    const work = agents?.[pending.id] ?? []
    const total = Math.max(partsOf(pending.function.arguments).length, work.length)
    if (!total) return delegated ? 'Filling gaps' : 'Researching'
    const done = partsDone(work.filter((w) => w.status === 'done').length, total)
    return delegated ? `Filling gaps: ${done}` : `Researching ${total} part${total === 1 ? '' : 's'}: ${done}`
  }
  if (pending) return `${delegated ? 'Filling gaps' : 'Researching'}: ${toolTitle(pending.function.name)}`
  return delegated || calls.length ? 'Writing the report' : 'Planning the research'
}
