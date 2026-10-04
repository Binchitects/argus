import type { LiveArena } from './live'
import type { ChatTree } from './tree'
import type { ArenaMatch, ChatModel, FeedbackReason, Message } from './types'

/** Why an answer was bad, as the thumbs down offers it. */
export const feedbackReasons: { value: FeedbackReason; label: string }[] = [
  { value: 'wrong', label: 'Wrong' },
  { value: 'incomplete', label: 'Incomplete' },
  { value: 'too_long', label: 'Too long' },
  { value: 'unsafe', label: 'Unsafe' },
  { value: 'ignored_instructions', label: 'Ignored instructions' },
  { value: 'other', label: 'Other' },
]

/** A reason's words; "No reason given" for none. */
export const reasonLabel = (r: string | null) => feedbackReasons.find((x) => x.value === r)?.label ?? (r === 'none' || !r ? 'No reason given' : r)

/** Compare the next question: two models chosen, or (null) two at random. */
export interface CompareChoice {
  models: [string, string] | null
}

/** The models a comparison can draw from: those that can answer now. */
export const comparable = (models: ChatModel[]) => models.filter((m) => m.loaded || m.onRequest)

/** A comparison as the page shows it: what is saved, and the side answering now while it streams. */
export interface ArenaView extends Omit<ArenaMatch, 'a' | 'b'> {
  a: string | null
  b: string | null
  /** The side answering now (while it streams), with its step. */
  answering: 'a' | 'b' | null
  step?: number
  of?: number
}

/** The saved comparison of a question and the one streaming, as one; null when the question was not compared. */
export function arenaView(question: Message, saved: ArenaMatch[] | undefined, live: LiveArena | null | undefined, answering: boolean): ArenaView | null {
  const kept = saved?.find((a) => a.questionId === question.id)
  const now = live?.questionId === question.id ? live : null
  if (!kept && !now) return null
  return {
    id: kept?.id ?? now!.id, questionId: question.id, a: now?.a ?? kept?.a ?? null, b: now?.b ?? kept?.b ?? null,
    vote: kept?.vote ?? null, models: kept?.models ?? null, answering: answering && now ? now.side : null, step: now?.step, of: now?.of,
  }
}

/** One side's answer: from its first message down its newest line, up to the next question. */
export function sideMessages(tree: ChatTree, first: string | null): Message[] {
  const out: Message[] = []
  for (let m = first ? tree.byId.get(first) : undefined; m && m.role !== 'user'; m = tree.childrenOf(m.id).filter((k) => k.role !== 'user').at(-1)) out.push(m)
  return out
}
