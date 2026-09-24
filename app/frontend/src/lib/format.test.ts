import { describe, expect, it } from 'vitest'
import { supportHref } from './api'
import { ago, count, formatValue, money } from './format'

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


describe('formatValue (Grafana units)', () => {
  it('formats the units the dashboards use', () => {
    expect(formatValue(1234567)).toBe('1.23 M')
    expect(formatValue(0.000102)).toBe('0.000102')
    expect(formatValue(0.25, 'percentunit')).toBe('25.0%')
    expect(formatValue(1536, 'bytes')).toBe('1.5 KiB')
    expect(formatValue(0.004, 'currencyUSD')).toBe('$0.004')
    expect(formatValue(12.5, 'currencyUSD')).toBe('$12.50')
    expect(formatValue(0.25, 's')).toBe('250 ms')
    expect(formatValue(null)).toBe('—')
  })
})

describe('support contact', () => {
  it('links an email or a web address, and nothing else', () => {
    expect(supportHref('it@example.test')).toBe('mailto:it@example.test')
    expect(supportHref('https://help.example.test/llm')).toBe('https://help.example.test/llm')
    expect(supportHref('javascript:alert(1)')).toBeNull()
    expect(supportHref('Room 4, ask Sam')).toBeNull()
  })
})
