import type { ReactNode } from 'react'
import { formatValue } from '@/lib/format'
import type { Compaction, ContextFill, Message } from './types'

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
 * The room kept for the answer, as the server keeps it (PromptBudget.Room): the chat's own longest answer when it set one
 * (at most half the context); else an eighth of the context, at most the model's longest answer, at least 1,024 tokens (a
 * quarter of a tiny context at most). Null when the context is not known.
 */
export function answerRoom(context: number | null | undefined, maxOutput: number | null | undefined, chosen: number | null | undefined): number | null {
  if (!context) return null
  if (chosen && chosen > 0) return Math.min(chosen, Math.floor(context / 2))
  return Math.max(Math.min(Math.floor(context / 8), maxOutput && maxOutput > 0 ? maxOutput : Infinity), Math.min(1024, Math.floor(context / 4)))
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

/**
 * When the chat compacts itself: at `at` percent of the context (0: only when a message would not fit), whose choice
 * that is (`who`: "your choice", "the company's", "from your Arena account"), and where to change it.
 */
export interface CompactSetting {
  at: number
  who: string
  change?: ReactNode
}

/** What the gauge says of when the chat compacts itself. */
export function compactsText(c: CompactSetting): string {
  return c.at > 0 ? `Compacts at ${c.at}% (${c.who}), and always before a message would not fit.` : `Compacts only when a message would not fit (${c.who}).`
}

/** What a compaction mark says: why the chat compacted there, when the page saw it happen. */
export function compactedText(c: Compaction | null | undefined): string {
  const share = c?.tokens && c.context ? Math.round((c.tokens / c.context) * 100) : null
  const size = c?.tokens && c.context ? ` (about ${formatValue(c.tokens)} of ${formatValue(c.context)} tokens)` : ''
  switch (c?.reason) {
    case 'threshold':
      return share ? `Compacted at ${share}% of the context: the model reads a summary of the chat above` : 'Compacted near the end of the context: the model reads a summary of the chat above'
    case 'fit':
      return `Compacted so your message fits${size}: the model reads a summary of the chat above`
    case 'overflow':
      return 'The model could not read the whole chat: compacted and asked again. It reads a summary of the chat above'
    default:
      return 'Compacted: the model reads a summary of the chat above'
  }
}

/** An answer that failed because the chat, or the message, is too long for the model even compacted. */
export function contextError(m: Message): boolean {
  return m.errorKind === 'context' || /^(This chat is longer than the model can read|Your message alone is about)/.test(m.error ?? '')
}
