import { describe, expect, it } from 'vitest'
import { CLOSING, CYCLE, EYE_RATIO, eyeCap, eyeFrames, nextShown, OPENING, placeEyes, type Box, type Eye } from './eyes'

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

  it('get fewer on a phone than on a laptop, and never more than 160 a screenful', () => {
    expect(eyeCap(390)).toBeLessThan(eyeCap(1024))
    expect(eyeCap(1024)).toBeLessThan(eyeCap(1440))
    expect(eyeCap(3840)).toBe(160)
    // Even on a page two screens high (a small phone), as many a screenful.
    expect(placeEyes(390, 1688, { x: 195, y: 100 }, [], 844).length).toBeLessThanOrEqual(2 * eyeCap(390))
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

  // The form grows when it shows an error, field errors or the next step.
  for (const [name, width, height, form] of [
    ['a laptop', 1440, 900, { left: 880, top: 250, right: 1250, bottom: 650 }],
    ['a phone', 390, 844, { left: 16, top: 250, right: 374, bottom: 700 }],
  ] as const)
    it(`stay where they are on ${name} when the form grows, but for those beside it`, () => {
      const origin = { x: width / 3, y: 120 }
      const taller = { ...form, top: form.top - 20, bottom: form.bottom + 20 }
      const before = placeEyes(width, height, origin, [form])
      const after = placeEyes(width, height, origin, [taller])
      const by = new Map(after.map((e) => [e.key, e]))
      const nearForm = (e: Eye) => e.x + e.width > taller.left && e.x - e.width < taller.right && e.y + e.width > taller.top && e.y - e.width < taller.bottom
      const kept = before.filter((e) => !nearForm(e))
      expect(kept.length).toBeGreaterThan(before.length / 2)
      // Every eye not beside the form: the same place, size and times.
      for (const e of kept) expect(by.get(e.key), e.key).toEqual(e)
      // Only some beside it went, and none came.
      expect(after.length).toBeLessThan(before.length)
      expect(after.every((e) => before.some((b) => b.key === e.key))).toBe(true)
    })

  it('stay where they are when the page grows past the screen (a small phone), with rows added below', () => {
    const origin = { x: 160, y: 90 }
    const form = { left: 16, top: 180, right: 304, bottom: 540 }
    const before = placeEyes(320, 624, origin, [form], 568)
    const after = placeEyes(320, 684, origin, [{ ...form, bottom: 600 }], 568)
    const by = new Map(after.map((e) => [e.key, e]))
    const above = before.filter((e) => e.y < form.top)
    expect(above.length).toBeGreaterThan(4)
    for (const e of above) expect(by.get(e.key), e.key).toEqual(e)
    expect(Math.max(...after.map((e) => e.y))).toBeGreaterThan(Math.max(...before.map((e) => e.y)))
  })

  it('that went are drawn a moment longer, to fade out; one that comes back is drawn as ever', () => {
    const eyes = placeEyes(1440, 900, { x: 300, y: 300 }, [])
    const [gone, ...rest] = eyes
    const shown = nextShown(eyes, rest)
    expect(shown).toHaveLength(eyes.length)
    expect(shown.filter((e) => e.going).map((e) => e.key)).toEqual([gone.key])
    expect(nextShown(shown, rest)).toEqual(shown)
    const back = nextShown(shown, eyes)
    expect(back).toHaveLength(eyes.length)
    expect(back.some((e) => e.going)).toBe(false)
  })

  it('are the same every time for the same screen', () => {
    const one = placeEyes(1280, 800, { x: 300, y: 300 }, [])
    expect(placeEyes(1280, 800, { x: 300, y: 300 }, [])).toEqual(one)
  })

  it('are none on a stage with no size', () => {
    expect(placeEyes(0, 0, { x: 0, y: 0 }, [])).toEqual([])
  })
})
