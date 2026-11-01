import { describe, expect, it } from 'vitest'
import { avatarColor } from './avatar'

/** oklch(L C h) to linear sRGB, each channel clamped into the gamut as a browser shows it. */
function linear(css: string): number[] {
  const [L, C, h] = css.match(/oklch\(([\d.]+) ([\d.]+) ([\d.]+)\)/)!.slice(1).map(Number) as [number, number, number]
  const a = C * Math.cos((h * Math.PI) / 180)
  const b = C * Math.sin((h * Math.PI) / 180)
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3
  return [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s]
    .map((c) => Math.min(1, Math.max(0, c)))
}

describe('Avatar', () => {
  it('keeps its white initials at 4.5:1 or more on every hue a name can pick', () => {
    let worst = Infinity
    for (let hue = 0; hue < 360; hue++) {
      const [r, g, b] = linear(avatarColor(hue))
      worst = Math.min(worst, 1.05 / (0.2126 * r! + 0.7152 * g! + 0.0722 * b! + 0.05))
    }
    expect(worst).toBeGreaterThanOrEqual(4.5)
  })
})
