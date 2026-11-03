import type { ReactNode } from 'react'
import { formatValue } from '@/lib/format'
import type { Compaction, ContextFill, ContextItem, MeasuredContext, Message } from './types'

/** What fills the context, in the order it is drawn (each keeps its colour by kind). */
/** What fills the context, each with its hue of the categorical palette (8): the memory shares the instructions', hatched. */
export const kinds: { key: keyof ContextFill; label: string; hue: number; hatch?: boolean }[] = [
  { key: 'system', label: 'System prompt and tool notes', hue: 0 },
  { key: 'tools', label: 'Tool definitions', hue: 1 },
  { key: 'instructions', label: 'Your instructions', hue: 2 },
  { key: 'memory', label: 'Memory', hue: 2, hatch: true },
  { key: 'summary', label: 'Summary of earlier messages', hue: 3 },
  { key: 'files', label: 'Files', hue: 4 },
  { key: 'you', label: 'Your messages', hue: 5 },
  { key: 'answers', label: 'Answers', hue: 6 },
  { key: 'toolResults', label: 'Tool calls and results', hue: 7 },
]

/** A kind's fill: its hue, hatched where it shares one. */
export function kindFill(kind: { hue: number; hatch?: boolean }, colors: readonly string[]): string {
  const color = colors[kind.hue] ?? 'currentColor'
  return kind.hatch ? `repeating-linear-gradient(135deg, ${color} 0 2px, transparent 2px 4px)` : color
}

export interface ContextView {
  /** Tokens the next request starts from: the last one's prompt and what was answered to it. */
  used: number
  limit: number
  /** Tokens kept for the answer (the chat's or the model's longest). */
  output: number | null
  /** By kind, in tokens; null for answers from before the breakdown was kept. */
  parts: { key: keyof ContextFill; label: string; tokens: number }[] | null
  /** Worked out, not counted: the chat was compacted since the last answer, or that answer's request was not measured (the next one counts it). */
  estimated?: boolean
  /** The chat was compacted since the last answer. */
  compacted?: boolean
  /** The messages that took most of it, the biggest first, in tokens. */
  biggest?: { item: ContextItem; tokens: number }[]
}

/**
 * The room kept for the answer, as the server keeps it (PromptBudget.Room): what the request asks for when the chat set its
 * own longest answer, within chatAnswerMost; else an eighth of the context, at most the model's longest answer, at least
 * 1,024 tokens (a quarter of a tiny context at most). Null when the context is not known.
 */
export function answerRoom(context: number | null | undefined, maxOutput: number | null | undefined, chosen: number | null | undefined): number | null {
  if (!context) return null
  const most = maxOutput && maxOutput > 0 ? maxOutput : null
  if (chosen && chosen > 0) return Math.min(chosen, chatAnswerMost(context, maxOutput)!)
  return Math.max(Math.min(Math.floor(context / 8), most ?? Infinity), Math.min(1024, Math.floor(context / 4)))
}

/** Characters by kind as tokens: `tokens` shared out by what each kind takes, rounded so they add up to it. */
function shared(fill: ContextFill, chars: number, tokens: number): { key: keyof ContextFill; label: string; tokens: number }[] {
  let sum = 0
  let before = 0
  return kinds.map((k) => {
    sum += fill[k.key] ?? 0
    const upTo = Math.round((sum / chars) * tokens)
    const part = { ...k, tokens: upTo - before }
    before = upTo
    return part
  })
}

/**
 * The next request as Code Arena measures it while no turn runs: by kind in characters (the note it adds, and the
 * biggest messages, included), and the tokens it holds about (counted for what the gateway counted, worked out for
 * what was added since).
 */
export interface MeasuredNow {
  fill: MeasuredContext
  tokens: number
}

/**
 * The longest a chat's own longest answer may be, as the server takes it (PromptBudget.Most): the model's, and at most half
 * its context, so the conversation keeps the other half to read. Null when neither is known.
 */
export function chatAnswerMost(context: number | null | undefined, maxOutput: number | null | undefined): number | null {
  const own = maxOutput && maxOutput > 0 ? maxOutput : null
  const half = context ? Math.floor(context / 2) : null
  return own != null && half != null ? Math.min(own, half) : (own ?? half)
}

/**
 * How full the model's context is, from the last answer: its prompt as the model
 * counted it, split by what filled it (measured in characters, scaled to that count),
 * and what it answered (the next question carries it). With `now` (Code Arena's own
 * measure of the next request), from that instead: the conversation as it stands.
 */
export function contextOf(last: Message | undefined, limit: number | null | undefined, output: number | null, compacted?: string | null, now?: MeasuredNow): ContextView | undefined {
  if (now && limit) {
    const chars = kinds.reduce((n, k) => n + (now.fill[k.key] ?? 0), 0)
    const biggest = now.fill.biggest?.length && chars > 0 ? now.fill.biggest.map((item) => ({ item, tokens: Math.round((item.chars / chars) * now.tokens) })) : undefined
    // Not what the model counted when the chat compacted since its last answer, or that answer was written elsewhere (the
    // web chat's, with its own prompt and tools): the next answer counts it.
    const estimated = compacted != null || !last?.context
    return { used: now.tokens, limit, output, parts: chars > 0 ? shared(now.fill, chars, now.tokens) : null, biggest, estimated, compacted: compacted != null }
  }
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
    const kept: ContextFill = { system: fill?.system ?? 0, tools: fill?.tools ?? 0, instructions: fill?.instructions ?? 0, memory: fill?.memory ?? 0, summary: compacted.length + 150, files: 0, you: 0, answers: 0, toolResults: 0 }
    const estimated = kinds.map((k) => ({ ...k, tokens: Math.round((kept[k.key] ?? 0) * perChar) }))
    return { used: estimated.reduce((n, p) => n + p.tokens, 0), limit, output, parts: estimated, estimated: true, compacted: true }
  }
  const parts = fill && chars > 0 ? shared(fill, chars, prompt).map((p) => (p.key === 'answers' ? { ...p, tokens: p.tokens + completion } : p)) : null
  const biggest = fill?.biggest?.length && chars > 0 ? fill.biggest.map((item) => ({ item, tokens: Math.round((item.chars / chars) * prompt) })) : undefined
  return { used: prompt + completion, limit, output, parts, biggest }
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
