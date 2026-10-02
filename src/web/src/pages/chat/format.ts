import type { ChatConfig, Message } from './types'

/** "0.4 s", "12 s", "1 min 5 s", "1 h 12 min". */
export function seconds(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return ''
  if (ms < 10_000) return `${(ms / 1000).toFixed(1)} s`
  const s = Math.round(ms / 1000)
  if (s >= 3600) return `${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`
}

/** Tools whose function name says less than their title. */
const titles: Record<string, string> = { delegate: 'Sub-agents', ask_user: 'Questions for you' }

/** "find_symbol" -> "Find symbol"; "delegate" -> "Sub-agents". */
export function toolTitle(name: string): string {
  if (Object.hasOwn(titles, name)) return titles[name]!
  const words = name.replace(/[_-]+/g, ' ').trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/** What an answer cost, from its tokens and its model's prices. */
export function answerCost(messages: Message[], config: ChatConfig): number | null {
  let total = 0
  let known = false
  for (const m of messages) {
    const p = config.models.find((x) => x.name === m.model)?.prices
    if (!p || m.promptTokens === null || p.input === null || p.output === null) continue
    const cached = m.cachedTokens ?? 0
    total += ((m.promptTokens - cached) * p.input + cached * (p.cachedInput ?? p.input) + (m.completionTokens ?? 0) * p.output) / 1_000_000
    known = true
  }
  return known ? total : null
}

/** Today, Yesterday, Previous 7 days, Previous 30 days, then by month. */
export function bucket(iso: string, now = new Date()): string {
  const d = new Date(iso)
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
  const days = Math.round((day(now) - day(d)) / 86_400_000)
  if (days <= 0) return 'Today'
  if (days === 1) return 'Yesterday'
  if (days < 7) return 'Previous 7 days'
  if (days < 30) return 'Previous 30 days'
  return d.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
}

/**
 * What a screen reader is told about the answer, in a status line: that it is being
 * written, which tool it uses, that it is ready or asks questions. Never the text as it
 * streams (a live thread would read every word again and again). Derived, so only a
 * change is announced: a chat opened is not.
 */
export function answerNews(answering: boolean, path: Message[]): string {
  const last = path.at(-1)
  const calls = path.filter((m) => m.role === 'assistant').flatMap((m) => m.toolCalls ?? [])
  const answered = new Set(path.filter((m) => m.role === 'tool').map((m) => m.toolCallId))
  const running = answering ? calls.findLast((c) => !answered.has(c.id)) : undefined
  if (running) return `Using ${toolTitle(running.function.name)}…`
  if (answering) return 'Writing the answer…'
  if (last?.role === 'tool' && last.toolName === 'ask_user' && last.status !== 'failed') return 'The answer asks you some questions.'
  if (last?.role === 'assistant' && last.status === 'complete') return 'Answer ready.'
  return ''
}
