import { describe, expect, it } from 'vitest'
import { commandFilter } from './command-filter'

describe('command filter', () => {
  it('matches the start of words, not scattered letters', () => {
    expect(commandFilter('Administration Audit log', 'audit')).toBeGreaterThan(0)
    expect(commandFilter('Workspace Usage & cost', 'audit', ['tokens', 'spend', 'credit', 'budget'])).toBe(0)
    expect(commandFilter('Workspace Usage & cost', 'cost')).toBeGreaterThan(0)
  })
  it('uses keywords, and needs every word', () => {
    expect(commandFilter('Administration Sign-in', 'ldap', ['ldap', 'directory'])).toBeGreaterThan(0)
    expect(commandFilter('Administration People', 'people admin')).toBeGreaterThan(0)
    expect(commandFilter('Administration People', 'people chat')).toBe(0)
  })
  it('ranks whole words above prefixes', () => {
    expect(commandFilter('Chat', 'chat')).toBeGreaterThan(commandFilter('Chatter', 'chat'))
  })
})

describe('command filter ranking', () => {
  it('a title match outranks a keyword match', () => {
    expect(commandFilter('Your account', 'account')).toBeGreaterThan(commandFilter('Administration People', 'account', ['users', 'accounts']))
  })
})
