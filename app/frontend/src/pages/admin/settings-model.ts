/** The Settings page's data, as /api/admin/config sends it. */
export interface SettingView {
  key: string
  group: string
  label: string
  help: string
  type: 'text' | 'wholenumber' | 'number' | 'boolean' | 'duration' | 'choice' | 'choices' | 'url' | 'secret'
  scope: 'live' | 'apprestart' | 'stack'
  options: string[] | null
  min: number | null
  max: number | null
  patternHelp: string | null
  unit: string | null
  optional: boolean
  impact: string | null
  dangerous: boolean
  default: string | null
  value: string | null
  isSet: boolean
  source: 'saved' | 'environment' | 'default' | 'stack'
  environmentValue: string | null
  pending: string | null
  pendingSet: boolean
  restartPending: boolean
}

export interface SettingsData {
  groups: { title: string; settings: SettingView[] }[]
  pendingStack: number
  restartNeeded: boolean
  pendingFileWritable: boolean
}

const unitSeconds: Record<string, number> = { minutes: 60, hours: 3600, days: 86400 }

/** .NET TimeSpan "c" format ([d.]hh:mm:ss[.fffffff]) to seconds. */
export function timeSpanSeconds(v: string | null | undefined): number | null {
  if (!v) return null
  const m = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(v.trim())
  if (!m) return null
  const s = Number(m[2] ?? 0) * 86400 + Number(m[3]) * 3600 + Number(m[4]) * 60 + Number(m[5])
  return m[1] ? -s : s
}

export function toTimeSpan(seconds: number): string {
  const s = Math.round(seconds)
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const mi = Math.floor((s % 3600) / 60)
  const se = s % 60
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d ? `${d}.` : ''}${pad(h)}:${pad(mi)}:${pad(se)}`
}

/** A duration in its display unit ("90" minutes), for the editor. */
export function durationInUnit(v: string | null, unit: string | null): string {
  const s = timeSpanSeconds(v)
  if (s === null) return ''
  const n = s / (unitSeconds[unit ?? 'minutes'] ?? 60)
  return String(Number(n.toFixed(2)))
}

/** The editor's text back to what the API takes; null when it is not a number. */
export function durationFromUnit(text: string, unit: string | null): string | null {
  const n = Number(text.trim())
  if (!text.trim() || !Number.isFinite(n) || n <= 0) return null
  return toTimeSpan(n * (unitSeconds[unit ?? 'minutes'] ?? 60))
}

/** What the editor starts with: the pending value when there is one, a secret never. */
export function initialValue(s: SettingView): string {
  if (s.type === 'secret') return ''
  const v = s.scope === 'stack' ? (s.pendingSet ? s.pending : s.value) : s.value
  return s.type === 'duration' ? durationInUnit(v, s.unit) : (v ?? '')
}

/** What is sent for an edited value. */
export function wireValue(s: SettingView, text: string): string {
  if (s.type === 'duration') return durationFromUnit(text, s.unit) ?? text
  return text
}

export function slug(title: string): string {
  return title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
}

export function bytesHint(v: string): string | null {
  const n = Number(v)
  if (!Number.isFinite(n) || n <= 0) return null
  return n >= 1048576 ? `${Number((n / 1048576).toFixed(1))} MB` : `${Number((n / 1024).toFixed(1))} KB`
}
