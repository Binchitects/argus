import { describe, expect, it } from 'vitest'
import { findNavItem, serviceUrl } from './nav'

describe('nav', () => {
  it('finds the page a path belongs to by the longest prefix', () => {
    expect(findNavItem('/')?.title).toBe('Home')
    expect(findNavItem('/admin')?.title).toBe('Overview')
    expect(findNavItem('/admin/people/123')?.title).toBe('People')
    expect(findNavItem('/chat/abc')?.title).toBe('Chat')
    expect(findNavItem('/nope')).toBeUndefined()
  })

  it('links a service beside this one, keeping the port', () => {
    expect(serviceUrl('metrics', { protocol: 'https:', host: 'llm.example.com' })).toBe('https://metrics.llm.example.com/')
    expect(serviceUrl('metrics', { protocol: 'https:', host: 'llm.example.com:8443' })).toBe('https://metrics.llm.example.com:8443/')
  })
})
