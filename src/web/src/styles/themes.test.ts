import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { colorThemeIds, colorThemes, type ColorThemeId } from '@/lib/color-themes'

// The colour themes' CSS (index.css), read as the browser would: each theme's light and dark block over the default's,
// var() resolved, OKLCH turned into sRGB, and WCAG's contrast computed from it.

type Rgb = [number, number, number]
interface Color {
  rgb: Rgb
  alpha: number
  /** In sRGB's gamut (a little either way, as rounding goes). */
  inGamut: boolean
  oklch: [number, number, number]
}
type Tokens = Record<string, string>

const css = readFileSync(join(process.cwd(), 'src/styles/index.css'), 'utf8')

/** Every top-level rule without nested blocks, by its selector: its custom properties, later rules over earlier ones. */
function rules(): Map<string, Tokens> {
  const out = new Map<string, Tokens>()
  for (const m of css.matchAll(/^([^\s{}@/*][^{}]*?)\s*\{\n([^{}]*?)^\}/gm)) {
    const props = out.get(m[1]!) ?? {}
    for (const p of m[2]!.matchAll(/(--[\w-]+):\s*([^;]+);/g)) props[p[1]!] = p[2]!.trim()
    out.set(m[1]!, props)
  }
  return out
}

const all = rules()
const defaultLight: Tokens = { ...all.get(':root') }
const defaultDark: Tokens = { ...defaultLight, ...all.get('.dark') }
const themed = colorThemeIds.filter((id) => id !== 'default')
const blockOf = (id: ColorThemeId, mode: 'light' | 'dark') => all.get(mode === 'light' ? `:root[data-color-theme='${id}']:not(.dark)` : `:root.dark[data-color-theme='${id}']`)
const tokensOf = (id: ColorThemeId, mode: 'light' | 'dark'): Tokens => ({ ...(mode === 'light' ? defaultLight : defaultDark), ...(id === 'default' ? {} : blockOf(id, mode)) })

function oklch(value: string): Color {
  const m = /^oklch\(([\d.]+) ([\d.]+) ([\d.]+)(?: \/ ([\d.]+))?\)$/.exec(value)
  if (!m) throw new Error(`not an OKLCH colour: ${value}`)
  const [L, C, h, alpha] = [Number(m[1]), Number(m[2]), Number(m[3]), m[4] ? Number(m[4]) : 1]
  const a = C * Math.cos((h * Math.PI) / 180)
  const b = C * Math.sin((h * Math.PI) / 180)
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const mm = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3
  const linear: Rgb = [4.0767416621 * l - 3.3077115913 * mm + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * mm - 0.3413193965 * s, -0.0041960863 * l - 0.7034186147 * mm + 1.707614701 * s]
  const encode = (x: number) => {
    const c = Math.min(1, Math.max(0, x))
    return c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055
  }
  return { rgb: linear.map(encode) as Rgb, alpha, inGamut: linear.every((x) => x >= -0.001 && x <= 1.001), oklch: [L, C, h] }
}

function color(tokens: Tokens, name: string): Color {
  const value = tokens[name]
  if (!value) throw new Error(`${name} is not set`)
  const ref = /^var\((--[\w-]+)\)$/.exec(value)
  return ref ? color(tokens, ref[1]!) : oklch(value)
}

/** A hex colour (lib/color-themes.ts), its OKLCH computed back from sRGB. */
function hex(h: string): Color {
  const rgb = [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255) as Rgb
  const [r, g, b] = rgb.map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)) as Rgb
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s
  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s
  return { rgb, alpha: 1, inGamut: true, oklch: [L, Math.hypot(A, B), ((Math.atan2(B, A) * 180) / Math.PI + 360) % 360] }
}

/** A colour at part opacity over another (Tailwind's bg-primary/10 over a card). */
const over = (top: Color, under: Color, alpha = top.alpha): Color => ({ ...under, rgb: top.rgb.map((c, i) => c * alpha + under.rgb[i]! * (1 - alpha)) as Rgb })

function luminance({ rgb }: Color) {
  const [r, g, b] = rgb.map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4))
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!
}
const contrast = (a: Color, b: Color) => {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p)
  return (x! + 0.05) / (y! + 0.05)
}

// Text and its ground: 4.5:1 (WCAG AA). The highlighting colours are code's text on code's grounds.
const text: [string, string][] = [
  ...['--background', '--card', '--popover', '--muted', '--secondary', '--accent', '--sidebar', '--sidebar-accent'].map((bg): [string, string] => ['--foreground', bg]),
  ...['--background', '--card', '--popover', '--muted', '--secondary', '--accent', '--sidebar', '--sidebar-accent'].map((bg): [string, string] => ['--muted-foreground', bg]),
  ['--card-foreground', '--card'],
  ['--popover-foreground', '--popover'],
  ['--primary-foreground', '--primary'],
  ['--primary-foreground', '--primary-hover'],
  ['--secondary-foreground', '--secondary'],
  ['--accent-foreground', '--accent'],
  ['--sidebar-foreground', '--sidebar'],
  ['--sidebar-foreground', '--sidebar-accent'],
  ['--destructive-foreground', '--destructive'],
  ['--destructive-foreground', '--destructive-hover'],
  ...['comment', 'keyword', 'string', 'number', 'function', 'type'].flatMap((h) => ['--muted', '--card', '--background'].map((bg): [string, string] => [`--hl-${h}`, bg])),
]

const every = colorThemeIds.flatMap((id) => (['light', 'dark'] as const).map((mode) => ({ id, mode })))

// The grounds things sit on, and the text on them.
const surfaces = ['--background', '--card', '--popover', '--secondary', '--muted', '--accent', '--border', '--input', '--sidebar', '--sidebar-accent', '--sidebar-border']
const texts = ['--foreground', '--card-foreground', '--popover-foreground', '--secondary-foreground', '--muted-foreground', '--accent-foreground', '--sidebar-foreground']
// The accent's shades (its hover, its text on tints, the focus ring) and the sign-in page's ground and lids.
const shades = ['--primary-hover', '--primary-ink', '--ring', '--eyes-bg', '--eyes-lid']

describe('colour themes', () => {
  it('has a light and a dark block for each theme the app offers, and no other', () => {
    const inCss = new Set([...all.keys()].flatMap((s) => /data-color-theme='(\w+)'/.exec(s)?.[1] ?? []))
    expect([...inCss].sort()).toEqual([...themed].sort())
    for (const id of themed) for (const mode of ['light', 'dark'] as const) expect(blockOf(id, mode), `${id} ${mode}`).toBeDefined()
    expect(colorThemes.map((t) => t.id)).toEqual([...colorThemeIds])
  })

  it('sets every colour of the default in each block, but the status colours, which mean state', () => {
    const own = Object.keys(defaultLight).filter((k) => (defaultLight[k]!.startsWith('oklch(') || k === '--shadow-color') && !/^--(destructive|success|warning|hl-)/.test(k))
    expect(own).toEqual(expect.arrayContaining(['--background', '--primary', '--primary-ink', '--sidebar-accent', '--eyes-bg', '--eyes-lid', '--shadow-color']))
    for (const id of themed) {
      for (const mode of ['light', 'dark'] as const) {
        const block = blockOf(id, mode)!
        expect(own.filter((k) => !(k in block)), `${id} ${mode}`).toEqual([])
        expect(Object.keys(block).filter((k) => /^--(destructive|success|warning|hl-)/.test(k)), `${id} ${mode}`).toEqual([])
        for (const [k, v] of Object.entries(block)) if (v.startsWith('oklch(')) expect(oklch(v).inGamut, `${id} ${mode} ${k}`).toBe(true)
      }
    }
  })

  it.each(every)('$id $mode: text 4.5:1 on its ground, accents 3:1, hover away from the text', ({ id, mode }) => {
    const t = tokensOf(id, mode)
    const low = text.map(([fg, bg]) => ({ pair: `${fg} on ${bg}`, ratio: contrast(color(t, fg), color(t, bg)) })).filter((p) => p.ratio < 4.5)
    expect(low).toEqual([])
    const bg = color(t, '--background')
    expect(contrast(color(t, '--primary'), bg)).toBeGreaterThanOrEqual(3)
    expect(contrast(color(t, '--primary'), color(t, '--card'))).toBeGreaterThanOrEqual(3)
    // The focus ring is the accent at part opacity: its colour stands out from the page.
    expect(contrast({ ...color(t, '--ring'), alpha: 1 }, bg)).toBeGreaterThanOrEqual(3)
    const pf = color(t, '--primary-foreground')
    expect(contrast(pf, color(t, '--primary-hover'))).toBeGreaterThanOrEqual(contrast(pf, color(t, '--primary')))
  })

  it.each(every)('$id $mode: each -ink colour 4.5:1 on its own tints, over the card and the page', ({ id, mode }) => {
    const t = tokensOf(id, mode)
    const low: string[] = []
    for (const name of ['primary', 'success', 'warning', 'destructive']) {
      const ink = color(t, `--${name}-ink`)
      for (const ground of ['--card', '--background']) {
        for (const share of [0, 0.05, 0.1, 0.15]) {
          const r = contrast(ink, over(color(t, `--${name}`), color(t, ground), share))
          if (r < 4.5) low.push(`--${name}-ink on ${name}/${share * 100} over ${ground}: ${r.toFixed(2)}`)
        }
      }
    }
    expect(low).toEqual([])
  })

  // Calm as the default: a theme's surfaces and text are the default's lightness and only as faintly tinted (its card
  // too, where the chart palette was validated); its accent is about as light and no more vivid, and so are the
  // accent's shades and the sign-in page's ground and lids.
  it.each(themed.flatMap((id) => (['light', 'dark'] as const).map((mode) => ({ id, mode }))))(
    '$id $mode: its surfaces and text are tinted about as faintly as the default’s, its accent as strong',
    ({ id, mode }) => {
      const t = tokensOf(id, mode)
      const d = tokensOf('default', mode)
      const off: string[] = []
      for (const name of [...surfaces, ...texts]) {
        const [L, C] = color(t, name).oklch
        const [L0, C0] = color(d, name).oklch
        if (Math.abs(L - L0) > 0.02 || Math.abs(C - C0) > 0.004) off.push(`${name}: oklch(${L} ${C}), the default's oklch(${L0} ${C0})`)
      }
      for (const name of shades) {
        const [L, C] = color(t, name).oklch
        const [L0, C0] = color(d, name).oklch
        if (Math.abs(L - L0) > 0.03 || C > C0 + 0.004) off.push(`${name}: oklch(${L} ${C}), the default's oklch(${L0} ${C0})`)
      }
      expect(off).toEqual([])
      const [L, C] = color(t, '--primary').oklch
      const [L0, C0] = color(d, '--primary').oklch
      expect(Math.abs(L - L0)).toBeLessThanOrEqual(0.03)
      expect(C).toBeLessThanOrEqual(C0)
      expect(C).toBeGreaterThanOrEqual(0.09)
    },
  )

  it.each(every)("$id $mode: the editor's and terminal's hex are the CSS's", ({ id, mode }) => {
    const t = tokensOf(id, mode)
    const palette = colorThemes.find((c) => c.id === id)![mode]
    for (const name of ['background', 'foreground', 'card', 'muted', 'border', 'primary', 'primaryInk', 'primaryForeground'] as const) {
      const css = color(t, `--${name.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)}`).rgb.map((c) => Math.round(c * 255))
      const table = hex(palette[name]).rgb.map((c) => Math.round(c * 255))
      expect(Math.max(...css.map((c, i) => Math.abs(c - table[i]!))), `${name}: ${palette[name]}`).toBeLessThanOrEqual(2)
    }
    // The line numbers have no CSS: text, 4.5:1 on the background, and only as faintly tinted as the default's.
    expect(contrast(hex(palette.lineNumber), hex(palette.background))).toBeGreaterThanOrEqual(4.5)
    const chroma = hex(palette.lineNumber).oklch[1]
    const defaultChroma = hex(colorThemes.find((c) => c.id === 'default')![mode].lineNumber).oklch[1]
    expect(Math.abs(chroma - defaultChroma), `lineNumber ${palette.lineNumber}`).toBeLessThanOrEqual(0.004)
  })

  it("offers what the server allows (Llm.Api/Identity/ColorThemes.cs)", () => {
    const cs = readFileSync(resolve(process.cwd(), '../Llm.Api/Identity/ColorThemes.cs'), 'utf8')
    expect(cs).toContain('public const string Default = "default";')
    const list = /All = \[Default, ([^\]]*)\]/.exec(cs)![1]!.split(',').map((s) => s.trim().replace(/"/g, ''))
    expect(['default', ...list]).toEqual([...colorThemeIds])
  })
})
