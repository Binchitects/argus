import { describe, expect, it } from 'vitest'
import { ago, count, money } from './format'

describe('format', () => {
  it('money never reads $0.00 for a real amount', () => {
    expect(money(12.5)).toBe('$12.50')
    expect(money(0.00437)).toBe('$0.00437')
    expect(money(0)).toBe('$0.00')
    expect(money(null)).toBe('—')
  })
  it('counts get compact when large', () => {
    expect(count(1234)).toBe('1,234')
    expect(count(1_234_567)).toBe('1.2M')
  })
  it('relative times', () => {
    const now = Date.UTC(2026, 8, 24, 12)
    expect(ago(now / 1000 - 10, now)).toBe('just now')
    expect(ago(now / 1000 - 3 * 3600, now)).toBe('3 hours ago')
    expect(ago(now / 1000 - 86400, now)).toBe('yesterday')
  })
})
