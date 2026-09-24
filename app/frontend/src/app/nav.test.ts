import { describe, expect, it } from 'vitest'
import { currentAppUrl, findNavItem } from './nav'

describe('nav', () => {
  it('finds the page a path belongs to by the longest prefix', () => {
    expect(findNavItem('/')?.title).toBe('Home')
    expect(findNavItem('/admin')?.title).toBe('Overview')
    expect(findNavItem('/admin/people/123')?.title).toBe('People')
    expect(findNavItem('/chat/abc')?.title).toBe('Chat')
    expect(findNavItem('/nope')).toBeUndefined()
  })

  it('points next.<domain> at the current app on the bare domain', () => {
    expect(currentAppUrl('/admin/people', { protocol: 'https:', host: 'next.llm.example.com' })).toBe('https://llm.example.com/admin/people')
    expect(currentAppUrl('/usage', { protocol: 'https:', host: 'llm.example.com:8443' })).toBe('https://llm.example.com:8443/usage')
  })
})
