import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { findNavItem, navigation, serviceUrl, visibleNavigation } from './nav'

/** The features deploy/features lists, by id. */
const features = readFileSync(`${process.cwd()}/../../deploy/features`, 'utf8')
  .split('\n')
  .filter((l) => l.trim() && !l.startsWith('#'))
  .map((l) => l.split('|')[0]!.trim())

describe('nav', () => {
  it('finds the page a path belongs to by the longest prefix', () => {
    expect(findNavItem('/')?.title).toBe('Home')
    expect(findNavItem('/admin')?.title).toBe('Overview')
    expect(findNavItem('/admin/people/123')?.title).toBe('People')
    expect(findNavItem('/chat/abc')?.title).toBe('Chat')
    expect(findNavItem('/nope')).toBeUndefined()
  })

  it('shows Dashboards to whoever holds any of its areas, and Line to who reads usage', () => {
    const titles = (permissions: string[]) => visibleNavigation({ isAdmin: false, permissions }).flatMap((s) => s.items.map((i) => i.title))
    for (const p of ['usage.read', 'monitoring.read', 'hardware.read']) expect(titles([p]), p).toContain('Dashboards')
    expect(titles(['hardware.read'])).not.toContain('Line')
    expect(titles(['usage.read'])).toContain('Line')
    expect(titles(['audit.read'])).not.toContain('Dashboards')
  })

  it('leaves out the pages of the features that are off, and names only features deploy/features lists', () => {
    const titles = (off: string[]) => visibleNavigation({ isAdmin: true, featuresOff: off }).flatMap((s) => s.items.map((i) => i.title))
    expect(titles([])).toContain('Science lab')
    const without = titles(['lab', 'code-intel', 'monitoring'])
    expect(without).not.toContain('Science lab')
    expect(without).not.toContain('Indexing')
    expect(without).not.toContain('Logs')
    expect(without).toContain('Chat')
    // The code-intel section has nothing left, so it is gone.
    expect(visibleNavigation({ isAdmin: true, featuresOff: ['code-intel'] }).map((s) => s.title)).not.toContain('Farzan Graph')
    for (const item of navigation.flatMap((s) => s.items)) if (item.feature) expect(features, item.title).toContain(item.feature)
  })

  it('leaves out the pages whose feature permissions a person holds none of', () => {
    const titles = (denied: string[]) => visibleNavigation({ isAdmin: false, denied }).flatMap((s) => s.items.map((i) => i.title))
    expect(titles([])).toEqual(expect.arrayContaining(['Chat', 'Decks', 'Code Studio', 'Scheduled tasks']))
    const without = titles(['feature.chat', 'feature.decks', 'feature.tasks', 'code.studio'])
    for (const t of ['Chat', 'Decks', 'Scheduled tasks']) expect(without).not.toContain(t)
    // Code Studio is for reviews too: shown while any of its permissions is held.
    expect(without).toContain('Code Studio')
    expect(titles(['code.studio', 'code.read', 'review.run'])).not.toContain('Code Studio')
    expect(findNavItem('/decks/d1')?.may).toEqual(['feature.decks'])
  })

  it('links a service beside this one, keeping the port', () => {
    expect(serviceUrl('metrics', { protocol: 'https:', host: 'llm.example.com' })).toBe('https://metrics.llm.example.com/')
    expect(serviceUrl('metrics', { protocol: 'https:', host: 'llm.example.com:8443' })).toBe('https://metrics.llm.example.com:8443/')
  })
})
