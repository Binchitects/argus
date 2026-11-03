import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

// public/theme-init.js, run as the page runs it: a plain script in <head>, before the stylesheet.
new Function(readFileSync(join(process.cwd(), 'public/theme-init.js'), 'utf8'))()

const link = (rel: string, href: string) => {
  const l = document.createElement('link')
  l.rel = rel
  l.href = href
  l.crossOrigin = ''
  document.head.append(l)
  return l
}

describe('theme-init.js', () => {
  it('fetches a stylesheet the browser failed once more, in its place, and only once', () => {
    const before = link('stylesheet', '/assets/fonts.css')
    const failed = link('stylesheet', '/assets/index-1.css')
    const after = link('stylesheet', '/assets/print.css')
    failed.dispatchEvent(new Event('error'))
    const sheets = () => [...document.head.querySelectorAll('link[rel=stylesheet]')]
    const again = sheets()[1]!
    expect(again).not.toBe(failed)
    expect(failed.isConnected).toBe(false)
    expect(again).toHaveAttribute('href', '/assets/index-1.css')
    expect(again).toHaveAttribute('crossorigin', '')
    expect(sheets()).toEqual([before, again, after])
    // Failed again (the file is gone): left as it is.
    again.dispatchEvent(new Event('error'))
    expect(sheets()).toEqual([before, again, after])
  })

  it('leaves what is not a stylesheet alone', () => {
    const icon = link('icon', '/favicon.svg')
    icon.dispatchEvent(new Event('error'))
    expect(icon.isConnected).toBe(true)
    const img = document.createElement('img')
    document.body.append(img)
    img.dispatchEvent(new Event('error'))
    expect(img.isConnected).toBe(true)
  })
})
