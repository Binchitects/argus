import { describe, expect, it } from 'vitest'
import { fold, sections, when } from './news'
import type { NewsItem } from './notifications'

const now = new Date('2026-10-22T12:00:00').getTime()
const item = (id: string, at: string, over: Partial<NewsItem> = {}): NewsItem => ({ id, kind: 'answer', title: id, body: null, link: '/chat/c1', createdAt: new Date(at).toISOString(), read: false, ...over })

describe("the bell's list", () => {
  it('says when in a few words', () => {
    expect(when(new Date(now - 20_000).toISOString(), now)).toBe('now')
    expect(when(new Date(now - 5 * 60_000).toISOString(), now)).toBe('5 min')
    expect(when(new Date(now - 3 * 3600_000).toISOString(), now)).toBe('3 h')
    expect(when(new Date('2026-10-21T09:30:00').toISOString(), now)).toMatch(/^Yesterday /)
  })

  it('folds news about the same place, and puts it under Today, Yesterday and Earlier', () => {
    const items = [
      item('a', '2026-10-22T11:59:00'), item('b', '2026-10-22T11:58:00'), item('c', '2026-10-22T11:00:00', { link: '/chat/c2' }),
      item('d', '2026-10-21T10:00:00', { kind: 'task', link: '/chat/c3' }), item('e', '2026-10-01T10:00:00', { kind: 'workflow', link: '/studio/w1' }),
    ]
    expect(fold(items).map((g) => [g.item.id, g.more.map((m) => m.id)])).toEqual([['a', ['b']], ['c', []], ['d', []], ['e', []]])
    // Not only side by side: news between them does not keep them apart.
    expect(fold([item('x', '2026-10-22T11:59:00'), item('y', '2026-10-22T11:58:00', { kind: 'download', link: '/admin/models' }), item('z', '2026-10-22T11:57:00')]).map((g) => [g.item.id, g.more.map((m) => m.id)]))
      .toEqual([['x', ['z']], ['y', []]])
    expect(sections(items, now).map((s) => [s.name, s.groups.map((g) => g.item.id)])).toEqual([['Today', ['a', 'c']], ['Yesterday', ['d']], ['Earlier', ['e']]])
  })
})
