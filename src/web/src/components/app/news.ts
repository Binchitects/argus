import type { NewsItem } from './notifications'

/** "now", "5 min", "3 h", then "Yesterday 14:05", then the date. */
export function when(at: string, now = Date.now()): string {
  const d = new Date(at)
  const s = Math.max(0, (now - d.getTime()) / 1000)
  if (s < 60) return 'now'
  if (s < 3600) return `${Math.floor(s / 60)} min`
  const today = new Date(now)
  if (d.toDateString() === today.toDateString()) return `${Math.floor(s / 3600)} h`
  const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  const yesterday = new Date(now - 86_400_000)
  if (d.toDateString() === yesterday.toDateString()) return `Yesterday ${time}`
  return d.toLocaleDateString([], { month: 'short', day: 'numeric', ...(d.getFullYear() !== today.getFullYear() ? { year: 'numeric' } : {}) })
}

/** Today, Yesterday or Earlier: the list's sections. */
export function day(at: string, now = Date.now()): string {
  const d = new Date(at).toDateString()
  if (d === new Date(now).toDateString()) return 'Today'
  if (d === new Date(now - 86_400_000).toDateString()) return 'Yesterday'
  return 'Earlier'
}

/** News of the same kind about the same place, the same day, is folded into the newest: it shows, with how many more. */
export function fold(items: NewsItem[]): { item: NewsItem; more: NewsItem[] }[] {
  const out: { item: NewsItem; more: NewsItem[] }[] = []
  const byKey = new Map<string, { item: NewsItem; more: NewsItem[] }>()
  for (const n of items) {
    const key = n.link ? `${n.kind}|${n.link}|${day(n.createdAt)}` : null
    const first = key ? byKey.get(key) : undefined
    if (first) {
      first.more.push(n)
      continue
    }
    const group = { item: n, more: [] as NewsItem[] }
    if (key) byKey.set(key, group)
    out.push(group)
  }
  return out
}

/** The folded news in its sections, in order: Today, Yesterday, Earlier. */
export function sections(items: NewsItem[], now = Date.now()): { name: string; groups: { item: NewsItem; more: NewsItem[] }[] }[] {
  const out: { name: string; groups: { item: NewsItem; more: NewsItem[] }[] }[] = []
  for (const g of fold(items)) {
    const name = day(g.item.createdAt, now)
    const last = out.at(-1)
    if (last?.name === name) last.groups.push(g)
    else out.push({ name, groups: [g] })
  }
  return out
}
