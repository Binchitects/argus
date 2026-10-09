/** One limit a minute and where it comes from: their own, a group's (named), the company's, or none set anywhere. */
export interface RateLimit {
  /** null: no limit. */
  value: number | null
  from: 'person' | 'group' | 'company' | 'none'
  group: string | null
}

/** A person's API key limits, as their key's page and their admin page show them. */
export interface KeyLimits {
  requestsPerMinute: RateLimit
  tokensPerMinute: RateLimit
  /** What an admin set for them; null: their groups' or the company's, 0: no limit. */
  own: { requestsPerMinute: number | null; tokensPerMinute: number | null }
  /** Requests a key may have at once; null: no limit. */
  atOnce: number | null
  /** What their keys used in the last minute; null when the gateway's log cannot be read. */
  used: { requests: number; tokens: number } | null
  /** What the gateway refused their keys in the last day, by limit. */
  refused: { limit: 'requests' | 'tokens' | 'at once' | 'other'; count: number; last: string }[] | null
}

/** The largest limits a minute (as the server checks them). */
export const maxRequests = 1_000_000
export const maxTokens = 1_000_000_000

/** A limit typed in a box: empty is null (not set here), else a whole number from 0 (no limit) to max. */
export function parseLimit(v: string, max: number): number | null | 'invalid' {
  const t = v.trim().replaceAll(',', '')
  if (t === '') return null
  const n = Number(t)
  return Number.isInteger(n) && n >= 0 && n <= max ? n : 'invalid'
}
