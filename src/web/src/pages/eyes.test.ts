import { describe, expect, it } from 'vitest'
import { CLOSING, CYCLE, EYE_RATIO, eyeCap, eyeFrames, OPENING, placeEyes, type Box } from './eyes'

const screens: [string, number, number][] = [
  ['a phone', 390, 844],
  ['a small laptop', 1024, 768],
  ['a laptop', 1440, 900],
  ['a large screen', 2560, 1440],
]

describe('the sign-in page’s eyes', () => {
  for (const [name, width, height] of screens)
    it(`fill ${name} without passing its cap`, () => {
      const eyes = placeEyes(width, height, { x: width / 3, y: height / 2 }, [])
      expect(eyes.length).toBeLessThanOrEqual(eyeCap(width))
      // Filled: well over half the cap, spread from edge to edge.
      expect(eyes.length).toBeGreaterThan(eyeCap(width) / 2)
      expect(Math.min(...eyes.map((e) => e.x))).toBeLessThan(width * 0.15)
      expect(Math.max(...eyes.map((e) => e.x))).toBeGreaterThan(width * 0.85)
      expect(Math.max(...eyes.map((e) => e.y))).toBeGreaterThan(height * 0.85)
    })

  it('get fewer on a phone than on a laptop, and never more than 120', () => {
    expect(eyeCap(390)).toBeLessThan(eyeCap(1024))
    expect(eyeCap(1024)).toBeLessThan(eyeCap(1440))
    expect(eyeCap(3840)).toBe(120)
  })

  it('keep off the form and the words', () => {
    const form: Box = { left: 880, top: 250, right: 1250, bottom: 650 }
    const words: Box = { left: 40, top: 740, right: 480, bottom: 860 }
    const eyes = placeEyes(1440, 900, { x: 340, y: 370 }, [form, words])
    expect(eyes.length).toBeGreaterThan(40)
    for (const e of eyes) {
      const h = e.width * EYE_RATIO
      for (const c of [form, words]) {
        const apart = e.x + e.width / 2 <= c.left || e.x - e.width / 2 >= c.right || e.y + h / 2 <= c.top || e.y - h / 2 >= c.bottom
        expect(apart, `${e.key} over ${JSON.stringify(c)}`).toBe(true)
      }
    }
  })

  it('open from the logo outward, close from the edges back in, all within one turn', () => {
    const origin = { x: 300, y: 400 }
    const eyes = placeEyes(1440, 900, origin, [])
    const by = (e: (typeof eyes)[number]) => Math.hypot(e.x - origin.x, e.y - origin.y)
    const sorted = [...eyes].sort((a, b) => by(a) - by(b))
    const near = sorted.slice(0, 5)
    const far = sorted.slice(-5)
    const mean = (xs: number[]) => xs.reduce((a, b) => a + b, 0) / xs.length
    expect(mean(near.map((e) => e.open))).toBeLessThan(mean(far.map((e) => e.open)))
    expect(mean(near.map((e) => e.close))).toBeGreaterThan(mean(far.map((e) => e.close)))
    for (const e of eyes) {
      expect(e.open).toBeGreaterThan(0)
      expect(e.open + OPENING).toBeLessThan(e.close)
      expect(e.close + CLOSING).toBeLessThan(CYCLE)
      const offsets = eyeFrames(e.open, e.close).map((f) => f.offset as number)
      expect(offsets).toEqual([...offsets].sort((a, b) => a - b))
    }
  })

  it('are the same every time for the same screen', () => {
    const one = placeEyes(1280, 800, { x: 300, y: 300 }, [])
    expect(placeEyes(1280, 800, { x: 300, y: 300 }, [])).toEqual(one)
  })

  it('are none on a stage with no size', () => {
    expect(placeEyes(0, 0, { x: 0, y: 0 }, [])).toEqual([])
  })
})
