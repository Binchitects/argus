import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { codeArenaBrowsers } from './browsers'

// public/browser-check.js, run as code-arena.html runs it: a plain script in <head>, before the page's modules.
const script = readFileSync(join(process.cwd(), 'public/browser-check.js'), 'utf8')
const run = () => new Function(script)()

function page() {
  document.body.innerHTML = '<div id="root"></div>'
}

describe('browser-check.js', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    document.body.innerHTML = ''
  })

  it('says nothing in a browser with what the page needs', () => {
    page()
    vi.stubGlobal('CSS', { supports: () => true })
    vi.stubGlobal('structuredClone', () => null)
    run()
    expect(document.querySelector('[role=alert]')).toBeNull()
  })

  it('says the browser is too old, and which are new enough, where the page would fail without a word', () => {
    page()
    // Chrome 104: no dvh, no container queries.
    vi.stubGlobal('CSS', { supports: (property: string) => property !== 'height' && property !== 'container-type' })
    vi.stubGlobal('structuredClone', () => null)
    run()
    const alert = document.querySelector('[role=alert]')!
    expect(alert).toHaveTextContent('This browser is too old for Code Arena')
    expect(alert).toHaveTextContent('Chrome or Edge 109 or newer, Firefox 115 or newer, or Safari 16 or newer')
    expect(alert.nextElementSibling).toBe(document.getElementById('root'))
  })

  it('looks for the browsers the build targets', () => {
    // The script and the build (vite.code-arena.config.ts) name the same versions.
    for (const b of codeArenaBrowsers) {
      const [, name, version] = /^([a-z]+)(\d+)$/.exec(b)!
      const said = { chrome: 'Chrome or Edge', edge: 'Chrome or Edge', firefox: 'Firefox', safari: 'Safari' }[name!]!
      expect(script).toMatch(new RegExp(`${said} ${version} or newer`))
    }
  })
})
