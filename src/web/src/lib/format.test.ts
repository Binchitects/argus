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

  it('names a reading of the machine in its unit, not as a bare or "billion" number', () => {
    expect(formatValue(53, 'celsius')).toBe('53 °C')
    expect(formatValue(49.29, 'celsius')).toBe('49.3 °C')
    expect(formatValue(59.04, 'celsius', 1)).toBe('59.0 °C')
    expect(formatValue(35.2, 'watt')).toBe('35 W')
    expect(formatValue(1500, 'watt')).toBe('1.50 kW')
    expect(formatValue(2.1e9, 'hertz')).toBe('2.1 GHz')
    expect(formatValue(9_751_000_000, 'hertz')).toBe('9.75 GHz')
    expect(formatValue(500e6, 'hertz')).toBe('500 MHz')
    expect(formatValue(0, 'hertz')).toBe('0 Hz')
    expect(formatValue(4700, 'rotmhz')).toBe('4.7 GHz')
    expect(formatValue(450e6, 'Bps')).toBe('450 MB/s')
    expect(formatValue(-1500, 'Bps')).toBe('-1.5 kB/s')
    expect(formatValue(12, 'Bps')).toBe('12 B/s')
    expect(formatValue(100e6, 'Bps', 0)).toBe('100 MB/s')
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
