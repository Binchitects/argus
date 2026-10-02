import { describe, expect, it } from 'vitest'
import { toBlocks } from './markdown-blocks'

const html = (text: string) => toBlocks(text).map((b) => (b.kind === 'html' ? b.html : '')).join('')

describe('right-to-left text', () => {
  it('gives each block its own direction', () => {
    const out = new DOMParser().parseFromString(html('سلام، این یک پاراگراف است.\n\nAn English paragraph.\n\n- مورد اول\n- مورد دوم\n\n> نقل قول'), 'text/html')
    expect([...out.body.children].map((e) => `${e.tagName} ${e.getAttribute('dir')}`)).toEqual(['P auto', 'P auto', 'UL auto', 'BLOCKQUOTE auto'])
    expect(out.querySelector('li')?.hasAttribute('dir')).toBe(false)
    expect(out.querySelector('blockquote p')?.hasAttribute('dir')).toBe(false)
  })

  it('keeps code left to right inside right-to-left text', () => {
    const out = new DOMParser().parseFromString(html('متن با `code` در میان\n\n- مورد\n\n  ```js\n  let x = 1\n  ```'), 'text/html')
    expect(out.querySelector('p code')?.getAttribute('dir')).toBe('ltr')
    expect(out.querySelector('pre')?.getAttribute('dir')).toBe('ltr')
  })
})
