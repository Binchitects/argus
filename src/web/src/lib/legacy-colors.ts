import type { Plugin } from 'vite'

// Colours with an alpha over a token (bg-primary/10, the selection's tint) are color-mix() over a var(). Chrome and Edge
// before 111 have no color-mix(), and the fallback Lightning CSS writes for them is the token at full strength: a 10% tint
// drawn solid, text on it unreadable. For those browsers each colour token gets its sRGB channels too (--primary-rgb: 36
// 116 207, beside --primary: #2474cf), and each such fallback takes them with the alpha: rgb(var(--primary-rgb) / 10%), in
// an @supports not (color-mix) of its own. Newer browsers skip it. The Code Arena build (vite.code-arena.config.ts) uses it.

const mixSupports = '@supports (color:color-mix(in lab, red, red)){'
const mix = /color-mix\(in (?:oklab|oklch|srgb|lab|lch), var\((--[\w-]+)\) (\d+(?:\.\d+)?%), transparent\)/g

/** A token's sRGB channels from its value as Lightning CSS writes it (#rgb, #rrggbb, opaque #rrggbbaa); null for others. */
function channels(value: string): string | null {
  const hex = /^#([0-9a-f]{3,8})$/i.exec(value)?.[1]
  if (!hex || hex.length === 5 || hex.length === 7) return null
  const full = hex.length <= 4 ? [...hex].map((c) => c + c).join('') : hex
  if (full.length === 8 && full.slice(6).toLowerCase() !== 'ff') return null
  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16)).join(' ')
}

/** The end of the block whose { is at open: the index of its }. */
function blockEnd(css: string, open: number): number {
  let depth = 0
  for (let i = open; i < css.length; i++) {
    if (css[i] === '{') depth++
    else if (css[i] === '}' && --depth === 0) return i
  }
  return css.length - 1
}

/** The stylesheet with every token's channels beside it, and a fallback with the alpha for each mix over one. */
export function legacyColors(css: string): string {
  // The tokens set to a colour (or to another such token), outside the @supports (lab) blocks that set them anew.
  const declaration = /([;{])(--[\w-]+):(#[0-9a-fA-F]{3,8}|var\((--[\w-]+)\))(?=[;}])/g
  const named = new Set<string>()
  for (const m of css.matchAll(declaration)) if (channels(m[3]!)) named.add(m[2]!)
  for (let more = true; more; ) {
    more = false
    for (const m of css.matchAll(declaration)) {
      if (m[4] && named.has(m[4]) && !named.has(m[2]!)) {
        named.add(m[2]!)
        more = true
      }
    }
  }
  let out = css.replace(declaration, (all, _at: string, name: string, value: string, of?: string) => {
    const rgb = of ? (named.has(of) ? `var(${of}-rgb)` : null) : channels(value)
    return rgb ? `${all};${name}-rgb:${rgb}` : all
  })

  // Each rule under @supports (color-mix): its declarations that mix a token with transparent, again with the channels.
  let result = ''
  for (let at = out.indexOf(mixSupports); at >= 0; at = out.indexOf(mixSupports)) {
    const end = blockEnd(out, at + mixSupports.length - 1)
    const inner = out.slice(at + mixSupports.length, end)
    const fallback = inner.replace(/([^{}]*)\{([^{}]*)\}/g, (_, selector: string, body: string) => {
      const kept = body
        .split(';')
        .filter((d) => {
          const mixes = [...d.matchAll(mix)]
          return mixes.length > 0 && mixes.every((m) => named.has(m[1]!)) && !d.replace(mix, '').includes('color-mix(')
        })
        .map((d) => d.replace(mix, (_m, name: string, alpha: string) => `rgb(var(${name}-rgb) / ${alpha})`))
      return kept.length ? `${selector}{${kept.join(';')}}` : ''
    })
    result += out.slice(0, at) + (fallback ? `@supports not (color:color-mix(in lab, red, red)){${fallback}}` : '') + out.slice(at, end + 1)
    out = out.slice(end + 1)
  }
  return result + out
}

/** legacyColors over every stylesheet of the build, as it is written. */
export function legacyColorsPlugin(): Plugin {
  return {
    name: 'legacy-colors',
    apply: 'build',
    enforce: 'post',
    generateBundle(_, bundle) {
      for (const file of Object.values(bundle)) {
        if (file.type === 'asset' && file.fileName.endsWith('.css')) file.source = legacyColors(String(file.source))
      }
    },
  }
}
