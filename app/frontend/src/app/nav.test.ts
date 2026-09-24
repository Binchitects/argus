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
    expect(serviceUrl('grafana', { protocol: 'https:', host: 'llm.example.com' })).toBe('https://grafana.llm.example.com/')
    expect(serviceUrl('grafana', { protocol: 'https:', host: 'llm.example.com:8443' })).toBe('https://grafana.llm.example.com:8443/')
  })
})
