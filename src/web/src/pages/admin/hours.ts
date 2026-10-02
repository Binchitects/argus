/** How working hours read: their days and times, in words. */

export const dayNames = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
export const fullDays = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']

/** "Mon–Fri", "Sat, Sun", "Every day". */
export function daysLabel(days: number[]): string {
  const d = [...days].sort((a, b) => a - b)
  if (d.length === 7) return 'Every day'
  const runs: string[] = []
  for (let i = 0; i < d.length; ) {
    let j = i
    while (j + 1 < d.length && d[j + 1] === d[j]! + 1) j++
    runs.push(j - i >= 2 ? `${dayNames[d[i]! - 1]}–${dayNames[d[j]! - 1]}` : d.slice(i, j + 1).map((x) => dayNames[x - 1]).join(', '))
    i = j + 1
  }
  return runs.join(', ')
}

/** "08:00–18:00", "22:00–06:00 (next day)", "all day". */
export function timesLabel(start: string, end: string): string {
  if (start === end) return 'all day'
  return `${start}–${end}${end < start ? ' (next day)' : ''}`
}
