import { describe, expect, it } from 'vitest'
import { LiveBlocks, sanitize, toBlocks } from './markdown-blocks'

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

describe('an answer read as it is written', () => {
  const answer = [
    '# The plan\n\nFirst we read **the file**, then we change it.\n\n',
    '- one\n- two\n\n  still two\n- three\n\n',
    'Title\n=====\n\n',
    '```ts title="src/a.ts"\nconst a = 1\n```\n\n',
    '| name | size |\n|:--|--:|\n| a.ts | 12 |\n| b.ts | 3 |\n\n',
    '> A quote\n> on two lines\n\n',
    'And $x^2$ to end, with a [link](https://example.test).\n',
  ].join('')

  it('shows at each step what the finished answer shows for that much of it', () => {
    const live = new LiveBlocks()
    for (let end = 1; end <= answer.length; end += 3) {
      const text = answer.slice(0, end)
      expect(live.update(text), text).toEqual(toBlocks(text))
    }
    expect(live.update(answer)).toEqual(toBlocks(answer))
  })

  it('reads it whole again when the text is not the one before with more after it', () => {
    const live = new LiveBlocks()
    live.update(answer)
    const other = 'Something else.\n\n- a\n- b\n'
    expect(live.update(other)).toEqual(toBlocks(other))
  })

  it('keeps a link defined early working in what comes after', () => {
    const live = new LiveBlocks()
    const text = 'See [the docs][d].\n\n[d]: https://example.test/docs\n\nMore.\n\nAnd [again][d].\n'
    for (let end = 1; end <= text.length; end += 2) live.update(text.slice(0, end))
    expect(live.update(text)).toEqual(toBlocks(text))
  })
})

describe('pictures in an answer', () => {
  it('shows a picture by its address, and only the words of one named by a bare file name', () => {
    expect(sanitize('<p><img src="https://example.test/a.png" alt="a"></p>')).toContain('<img')
    expect(sanitize('<p><img src="data:image/png;base64,AA" alt="b"></p>')).toContain('<img')
    expect(sanitize('<p>See <img src="a-hot-air-balloon.svg" alt="A balloon"> here</p>')).toBe('<p dir="auto">See A balloon here</p>')
  })
})
