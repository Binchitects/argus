import { weekdays } from '../tasks-schedule'

/** How a repository is brought up to date by itself, as Argus stores it: "", pass, off, hours:N, daily:HH:MM, weekly:D:HH:MM. */
export type ScheduleKind = 'default' | 'pass' | 'off' | 'hours' | 'daily' | 'weekly'

export interface RepoSchedule {
  kind: ScheduleKind
  /** Every N hours: 1–168. */
  hours: number
  /** HH:MM, in the index's time zone. */
  time: string
  /** ISO day: Monday 1 … Sunday 7. */
  day: number
}

const blank: RepoSchedule = { kind: 'pass', hours: 6, time: '02:00', day: 1 }
const two = (n: number) => String(n).padStart(2, '0')

/** The form for a stored schedule ("" is the default for all). */
export function scheduleForm(spec: string): RepoSchedule {
  const [kind, ...rest] = spec.trim().toLowerCase().split(':')
  const time = (h?: string, m?: string) => `${two(Number(h ?? 0))}:${two(Number(m ?? 0))}`
  switch (kind) {
    case '':
      return { ...blank, kind: 'default' }
    case 'off':
      return { ...blank, kind: 'off' }
    case 'hours':
      return { ...blank, kind: 'hours', hours: Number(rest[0]) || 6 }
    case 'daily':
      return { ...blank, kind: 'daily', time: time(rest[0], rest[1]) }
    case 'weekly':
      return { ...blank, kind: 'weekly', day: Number(rest[0]) || 1, time: time(rest[1], rest[2]) }
    default:
      return blank
  }
}

/** The stored form of a schedule. */
export function scheduleSpec(s: RepoSchedule): string {
  const [h = '0', m = '0'] = s.time.split(':')
  const at = `${two(Number(h))}:${two(Number(m))}`
  switch (s.kind) {
    case 'default':
      return ''
    case 'pass':
      return 'pass'
    case 'off':
      return 'off'
    case 'hours':
      return `hours:${s.hours}`
    case 'daily':
      return `daily:${at}`
    case 'weekly':
      return `weekly:${s.day}:${at}`
  }
}

/** A schedule in words, as Argus says it: "Every 6 hours", "Mondays at 02:30". */
export function scheduleWords(spec: string, defaultWords = 'the schedule for all'): string {
  const s = scheduleForm(spec)
  switch (s.kind) {
    case 'default':
      return `Same as all (${defaultWords.toLowerCase()})`
    case 'pass':
      return 'With each scheduled pass'
    case 'off':
      return 'Off: only pushes and when asked'
    case 'hours':
      return s.hours === 1 ? 'Every hour' : `Every ${s.hours} hours`
    case 'daily':
      return `Every day at ${s.time}`
    case 'weekly':
      return `${weekdays[s.day - 1]}s at ${s.time}`
  }
}
