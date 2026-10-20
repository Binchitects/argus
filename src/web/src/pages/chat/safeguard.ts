import { ApiError } from '@/lib/api'

/** A safeguard rule that asks the person before the message goes: to confirm (warn), or to give a reason (justify). */
export interface SafeguardQuestion {
  kind: 'warn' | 'justify'
  message: string
  rules: string[]
}

export interface SafeguardAnswer {
  go: boolean
  justification?: string
}

/** The rule's question from a refused send, when it is one the person may answer. */
export function safeguardQuestion(e: unknown): SafeguardQuestion | null {
  if (!(e instanceof ApiError) || (e.status !== 'safeguard_warn' && e.status !== 'safeguard_justify')) return null
  const rules = (e.data as { rules?: string[] } | undefined)?.rules ?? []
  return { kind: e.status === 'safeguard_warn' ? 'warn' : 'justify', message: e.message, rules }
}

/** The event a refusal can be reviewed by, when it has one. */
export function refusalEvent(e: unknown): string | null {
  return e instanceof ApiError && e.status.startsWith('safeguard_') ? ((e.data as { eventId?: string } | undefined)?.eventId ?? null) : null
}
