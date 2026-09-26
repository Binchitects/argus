import type { ChatConfig, Message } from './types'

/** "0.4 s", "12 s", "1 min 5 s". */
export function seconds(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return ''
  if (ms < 10_000) return `${(ms / 1000).toFixed(1)} s`
  const s = Math.round(ms / 1000)
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`
}

/** "find_symbol" -> "Find symbol". */
export function toolTitle(name: string): string {
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
