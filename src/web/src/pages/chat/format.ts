import type { ChatConfig, InLine, Message } from './types'

/** What an answer waiting for its turn says: whose line it is in, and how many go first. */
export function waitingText(line: InLine | null | undefined): string {
  if (line == null) return 'Waiting for the model…'
  if (line.yours) return 'Waiting for your other answer to end first.'
  const busy = line.model ? `${line.model} is busy` : 'The chat is busy'
  return line.ahead === 0 ? `${busy}: your turn is next.` : `${busy}: ${line.ahead} ${line.ahead === 1 ? 'answer' : 'answers'} ahead of you.`
}

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

/** The tokens an answer used, its sub-agents' with them, and what that cost (null: no price known). */
export interface AnswerUsage {
  prompt: number
  cached: number
  completion: number
  /** The sub-agents' share of the tokens above. */
  agents: { prompt: number; cached: number; completion: number }
  cost: number | null
  /** Some of the cost is worked out at today's prices: parts of the answer ran before costs were kept. */
  estimated: boolean
}

/**
 * What an answer used and cost: its rounds' tokens and its sub-agents' (kept with their
 * delegate call), and the cost each part kept when it ran (its tokens at the model's prices,
 * cached input at its own; the pictures, video or speech a tool made). A part from before
 * costs were kept is worked out at its model's prices now. The usage pages list the same.
 */
export function answerUsage(messages: Message[], config: ChatConfig): AnswerUsage {
  const u: AnswerUsage = { prompt: 0, cached: 0, completion: 0, agents: { prompt: 0, cached: 0, completion: 0 }, cost: null, estimated: false }
  const add = (cost: number) => (u.cost = (u.cost ?? 0) + cost)
  const estimate = (model: string | null | undefined, prompt: number, cached: number, completion: number) => {
    const p = config.models.find((x) => x.name === model)?.prices
    if (!p || p.input === null || p.output === null) return
    u.estimated = true
    add(((prompt - cached) * p.input + cached * (p.cachedInput ?? p.input) + completion * p.output) / 1_000_000)
  }
  for (const m of messages) {
    if (m.role === 'assistant' && m.promptTokens !== null) {
      u.prompt += m.promptTokens
      u.cached += m.cachedTokens ?? 0
      u.completion += m.completionTokens ?? 0
      if (m.cost !== null && m.cost !== undefined) add(m.cost)
      else estimate(m.model, m.promptTokens, m.cachedTokens ?? 0, m.completionTokens ?? 0)
    }
    if (m.role !== 'tool') continue
    const agents = (m.details?.agents ?? []).filter((a) => a.usage)
    for (const a of agents) {
      u.prompt += a.usage!.prompt
      u.cached += a.usage!.cached
      u.completion += a.usage!.completion
      u.agents.prompt += a.usage!.prompt
      u.agents.cached += a.usage!.cached
      u.agents.completion += a.usage!.completion
    }
    if (m.cost !== null && m.cost !== undefined) add(m.cost)
    else for (const a of agents) estimate(a.model, a.usage!.prompt, a.usage!.cached, a.usage!.completion)
  }
  return u
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

/** How many opened a shared chat, for its owner. */
export function opened(s: { opens: number; people: number }) {
  return s.opens === 0 ? 'Nobody has opened it yet.' : `Opened ${s.opens} ${s.opens === 1 ? 'time' : 'times'} by ${s.people} ${s.people === 1 ? 'person' : 'people'}.`
}
