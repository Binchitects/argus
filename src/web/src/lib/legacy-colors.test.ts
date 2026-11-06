import { browserslistToTargets, transform } from 'lightningcss'
import { compile } from 'tailwindcss'
import { describe, expect, it } from 'vitest'
import { codeArenaBrowsers } from './browsers'
import { legacyColors } from './legacy-colors'

// The Code Arena build's stylesheet as it is made: Tailwind's utilities and the page's own rules, then Lightning CSS with
// the build's targets, as Vite minifies it.
const targets = browserslistToTargets(codeArenaBrowsers.map((b) => b.replace(/^([a-z]+)(\d)/, '$1 $2')))
async function built(css: string, utilities: string[]) {
  const tailwind = (await compile(css)).build(utilities)
  return transform({ filename: 'a.css', code: Buffer.from(tailwind), minify: true, targets }).code.toString()
}

const source = `
@theme inline { --color-primary: var(--primary); --color-card-foreground: var(--card-foreground); --color-ring: var(--ring); }
@tailwind utilities;
:root { --primary: oklch(0.55 0.17 255); --card-foreground: var(--primary); --ring: currentcolor; }
.dark { --primary: oklch(0.7 0.15 255); }
::selection { background: color-mix(in oklch, var(--primary) 25%, transparent); }
`
const utilities = ['bg-primary/10', 'border-card-foreground/40', 'outline-ring/50']

describe('legacyColors', () => {
  it('the build alone draws a tint over a token at full strength where color-mix() is missing', async () => {
    const css = await built(source, utilities)
    // Chrome 109 skips the @supports block: what is left for it is the token itself, a 10% tint drawn solid.
    expect(css).toContain('.bg-primary\\/10{background-color:var(--primary)}')
    expect(css).toContain('@supports (color:color-mix(in lab, red, red)){.bg-primary\\/10{background-color:color-mix(in oklab, var(--primary) 10%, transparent)}}')
  })

  it('gives each colour token its channels and each tint over one an alpha of its own before color-mix()', async () => {
    const css = legacyColors(await built(source, utilities))
    // Beside each value, in each theme, and through a token set to another.
    expect(css).toMatch(/:root\{--primary:#[0-9a-f]{6};--primary-rgb:\d+ \d+ \d+;--card-foreground:var\(--primary\);--card-foreground-rgb:var\(--primary-rgb\)/)
    expect(css).toMatch(/\.dark\{--primary:#[0-9a-f]{6};--primary-rgb:\d+ \d+ \d+/)
    // The tint: its alpha where color-mix() is missing, the mix where it is not, in that order.
    const fallback = '@supports not (color:color-mix(in lab, red, red)){.bg-primary\\/10{background-color:rgb(var(--primary-rgb) / 10%)}}'
    expect(css).toContain(fallback)
    expect(css.indexOf(fallback)).toBeLessThan(css.indexOf('@supports (color:color-mix(in lab, red, red)){.bg-primary\\/10'))
    expect(css).toContain('{.border-card-foreground\\/40{border-color:rgb(var(--card-foreground-rgb) / 40%)}}')
    expect(css).toContain('{::selection{background:rgb(var(--primary-rgb) / 25%)}}')
    // A token that is no colour of its own (currentcolor) keeps the build's fallback.
    expect(css).not.toContain('--ring-rgb')
  })

  it('reads the channels of every colour Lightning CSS writes and leaves a translucent one alone', () => {
    const css = legacyColors(':root{--a:#fff;--b:#2474cf;--c:#2474cfff;--d:#2474cf80;--e:#0008}')
    expect(css).toBe(':root{--a:#fff;--a-rgb:255 255 255;--b:#2474cf;--b-rgb:36 116 207;--c:#2474cfff;--c-rgb:36 116 207;--d:#2474cf80;--e:#0008}')
  })

  it('leaves a stylesheet without tokens or mixes as it was', () => {
    const css = '.a{color:red}@supports (display:grid){.b{display:grid}}'
    expect(legacyColors(css)).toBe(css)
  })
})
