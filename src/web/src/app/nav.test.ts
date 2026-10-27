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

  it('leaves out the pages of the features that are off, and names only features deploy/features lists', () => {
    const titles = (off: string[]) => visibleNavigation({ isAdmin: true, featuresOff: off }).flatMap((s) => s.items.map((i) => i.title))
    expect(titles([])).toContain('Science lab')
    const without = titles(['lab', 'argus', 'monitoring'])
    expect(without).not.toContain('Science lab')
    expect(without).not.toContain('Indexing')
    expect(without).not.toContain('Logs')
    expect(without).toContain('Chat')
    // The Argus section has nothing left, so it is gone.
    expect(visibleNavigation({ isAdmin: true, featuresOff: ['argus'] }).map((s) => s.title)).not.toContain('Argus')
    for (const item of navigation.flatMap((s) => s.items)) if (item.feature) expect(features, item.title).toContain(item.feature)
  })

  it('links a service beside this one, keeping the port', () => {
    expect(serviceUrl('metrics', { protocol: 'https:', host: 'llm.example.com' })).toBe('https://metrics.llm.example.com/')
    expect(serviceUrl('metrics', { protocol: 'https:', host: 'llm.example.com:8443' })).toBe('https://metrics.llm.example.com:8443/')
  })
})
