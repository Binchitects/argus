import type { ContextFill, Message } from './types'

/** What fills the context, in the order it is drawn (each keeps its colour by kind). */
export const kinds: { key: keyof ContextFill; label: string }[] = [
  { key: 'system', label: 'System prompt and tool notes' },
  { key: 'tools', label: 'Tool definitions' },
  { key: 'instructions', label: 'Your instructions' },
  { key: 'summary', label: 'Summary of earlier messages' },
  { key: 'files', label: 'Files' },
  { key: 'you', label: 'Your messages' },
  { key: 'answers', label: 'Answers' },
  { key: 'toolResults', label: 'Tool calls and results' },
]

export interface ContextView {
  /** Tokens the next request starts from: the last one's prompt and what was answered to it. */
  used: number
  limit: number
  /** Tokens kept for the answer (the chat's or the model's longest). */
  output: number | null
  /** By kind, in tokens; null for answers from before the breakdown was kept. */
  parts: { key: keyof ContextFill; label: string; tokens: number }[] | null
  /** Worked out, not counted: the chat was compacted since the last answer (the next one counts it). */
  estimated?: boolean
}

/**
 * How full the model's context is, from the last answer: its prompt as the model
 * counted it, split by what filled it (measured in characters, scaled to that count),
 * and what it answered (the next question carries it).
 */
export function contextOf(last: Message | undefined, limit: number | null | undefined, output: number | null, compacted?: string | null): ContextView | undefined {
  if (!last || !limit || last.promptTokens == null) return undefined
  const prompt = last.promptTokens
  const completion = last.completionTokens ?? 0
  const fill = last.context
  const chars = fill ? kinds.reduce((n, k) => n + (fill[k.key] ?? 0), 0) : 0
  // Compacted since: the next request carries the same system prompt, tools and
  // instructions, and the summary instead of the messages, at the last request's
  // tokens per character (about 3.5 when nothing better is known).
  if (compacted != null) {
    const perChar = fill && chars > 0 ? prompt / chars : 1 / 3.5
    const kept: ContextFill = { system: fill?.system ?? 0, tools: fill?.tools ?? 0, instructions: fill?.instructions ?? 0, summary: compacted.length + 150, files: 0, you: 0, answers: 0, toolResults: 0 }
    const estimated = kinds.map((k) => ({ ...k, tokens: Math.round(kept[k.key] * perChar) }))
    return { used: estimated.reduce((n, p) => n + p.tokens, 0), limit, output, parts: estimated, estimated: true }
  }
  const parts = fill && chars > 0
    ? kinds.map((k) => ({ ...k, tokens: Math.round(((fill[k.key] ?? 0) / chars) * prompt) + (k.key === 'answers' ? completion : 0) }))
    : null
  return { used: prompt + completion, limit, output, parts }
}
