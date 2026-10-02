/** A task's schedule as people choose it (every day at 9, weekdays, …) and as cron, both ways. */

export type Repeat = 'daily' | 'weekdays' | 'weekly' | 'monthly' | 'hours' | 'custom'

export interface Schedule {
  repeat: Repeat
  /** HH:mm (for every N hours, only its minutes count). */
  time: string
  /** Weekly: ISO day, Monday 1 … Sunday 7. */
  day: number
  /** Monthly: the day of the month, 1–28 (every month has it). */
  date: number
  /** Every N hours. */
  hours: number
  cron: string
}

export const weekdays = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']

export const blankSchedule: Schedule = { repeat: 'weekdays', time: '09:00', day: 1, date: 1, hours: 4, cron: '0 9 * * 1-5' }

const two = (n: number) => String(n).padStart(2, '0')

export function cronOf(s: Schedule): string {
  const [h = 0, m = 0] = s.time.split(':').map(Number)
  switch (s.repeat) {
    case 'daily':
      return `${m} ${h} * * *`
    case 'weekdays':
      return `${m} ${h} * * 1-5`
    case 'weekly':
      return `${m} ${h} * * ${s.day % 7}`
    case 'monthly':
      return `${m} ${h} ${s.date} * *`
    case 'hours':
      return `${m} */${s.hours} * * *`
    case 'custom':
      return s.cron.trim()
  }
}

/** The form for a cron a task has: one of the choices when it is one, else custom. */
export function scheduleOf(cron: string): Schedule {
  const f = cron.trim().split(/\s+/)
  const base = { ...blankSchedule, cron: cron.trim(), repeat: 'custom' as Repeat }
  if (f.length !== 5 || !/^\d+$/.test(f[0]!)) return base
  const m = Number(f[0])
  if (/^\*\/\d+$/.test(f[1]!) && f[2] === '*' && f[3] === '*' && f[4] === '*') return { ...base, repeat: 'hours', hours: Number(f[1]!.slice(2)), time: `00:${two(m)}` }
  if (!/^\d+$/.test(f[1]!) || f[3] !== '*') return base
  const time = `${two(Number(f[1]))}:${two(m)}`
  if (f[2] === '*' && f[4] === '*') return { ...base, repeat: 'daily', time }
  if (f[2] === '*' && f[4] === '1-5') return { ...base, repeat: 'weekdays', time }
  if (f[2] === '*' && /^[0-7]$/.test(f[4]!)) return { ...base, repeat: 'weekly', time, day: Number(f[4]) % 7 || 7 }
  if (/^\d+$/.test(f[2]!) && Number(f[2]) <= 28 && f[4] === '*') return { ...base, repeat: 'monthly', time, date: Number(f[2]) }
  return base
}

/** "Weekdays at 09:00", "Mondays at 07:30", "Every 4 hours", or the cron itself. */
export function describe(cron: string): string {
  const s = scheduleOf(cron)
  switch (s.repeat) {
    case 'daily':
      return `Every day at ${s.time}`
    case 'weekdays':
      return `Weekdays at ${s.time}`
    case 'weekly':
      return `${weekdays[s.day - 1]}s at ${s.time}`
    case 'monthly':
      return `On day ${s.date} of each month at ${s.time}`
    case 'hours':
      return s.hours === 1 ? 'Every hour' : `Every ${s.hours} hours`
    case 'custom':
      return `Cron: ${s.cron}`
  }
}
